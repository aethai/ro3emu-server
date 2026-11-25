using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Additive economy receipt/ledger schema, separate from role lifecycle and owner revisions.</summary>
internal sealed class EconomySchemaPlan : DbPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "schema/economy";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        CreateCoreTables(db, tx);
        CreateRetentionTables(db, tx);
        AddUpgradeColumns(db, tx);
        RebuildLegacyLegCountConstraint(db, tx);
        using var seed = DbSql.Command(db, tx,
            "INSERT OR IGNORE INTO economy_meta(singleton,realm_id,next_sequence,retained_floor) VALUES(1,$realm,0,0)");
        seed.Parameters.AddWithValue("$realm", Guid.NewGuid().ToString("N"));
        seed.ExecuteNonQuery();
        return 1;
    }

    private static void CreateCoreTables(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS economy_meta (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                realm_id TEXT NOT NULL UNIQUE,
                next_sequence INTEGER NOT NULL CHECK(next_sequence>=0),
                retained_floor INTEGER NOT NULL DEFAULT 0 CHECK(retained_floor=0)
            );
            CREATE TABLE IF NOT EXISTS operation_receipt (
                operation_id TEXT PRIMARY KEY,
                realm_id TEXT NOT NULL,
                commit_seq INTEGER NOT NULL UNIQUE,
                actor_account TEXT NOT NULL,
                actor_role INTEGER NOT NULL,
                command_kind TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL,
                intent_hash TEXT NOT NULL,
                intent_payload BLOB NOT NULL,
                source_key TEXT NOT NULL UNIQUE,
                recorded_utc_ms INTEGER NOT NULL,
                effective_utc_ms INTEGER NOT NULL,
                outcome BLOB NOT NULL,
                child_receipts BLOB NOT NULL,
                before_revisions TEXT NOT NULL,
                after_revisions TEXT NOT NULL,
                leg_count INTEGER NOT NULL CHECK(leg_count>=0 AND (command_kind<>'npc-shop-buy' OR leg_count>0))
            );
            CREATE TABLE IF NOT EXISTS operation_source (
                realm_id TEXT NOT NULL,
                source_key TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                PRIMARY KEY(realm_id,source_key),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id)
            );
            CREATE TABLE IF NOT EXISTS economy_entry (
                entry_id INTEGER PRIMARY KEY AUTOINCREMENT,
                commit_seq INTEGER NOT NULL,
                operation_id TEXT NOT NULL,
                leg_no INTEGER NOT NULL,
                recorded_utc_ms INTEGER NOT NULL,
                effective_utc_ms INTEGER NOT NULL,
                owner_kind INTEGER NOT NULL,
                owner_id TEXT NOT NULL,
                compartment_kind INTEGER NOT NULL,
                compartment_key TEXT NOT NULL,
                instance_key TEXT NOT NULL,
                asset_kind INTEGER NOT NULL,
                asset_id INTEGER NOT NULL,
                asset_epoch TEXT NOT NULL,
                delta INTEGER NOT NULL,
                reason_code INTEGER NOT NULL,
                leg_kind INTEGER NOT NULL,
                edge_no INTEGER NOT NULL,
                counterparty_owner_kind INTEGER,
                counterparty_owner_id TEXT,
                counterparty_compartment_kind INTEGER,
                counterparty_compartment_key TEXT,
                counterparty_instance_key TEXT,
                before_fingerprint TEXT,
                after_fingerprint TEXT,
                source_key TEXT NOT NULL,
                UNIQUE(operation_id,leg_no),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id)
            );
            CREATE INDEX IF NOT EXISTS economy_entry_cut ON economy_entry(commit_seq,entry_id);
            -- Duplicate-source checks ask by source_key alone; the primary key starts with realm_id, so they scanned the table.
            CREATE INDEX IF NOT EXISTS operation_source_key ON operation_source(source_key);
            """);
    }

    private static void CreateRetentionTables(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS economy_daily_aggregate (
                day_utc TEXT NOT NULL,
                asset_kind INTEGER NOT NULL,
                asset_id INTEGER NOT NULL,
                reason_code INTEGER NOT NULL,
                leg_kind INTEGER NOT NULL,
                entry_count INTEGER NOT NULL,
                net_delta INTEGER NOT NULL,
                abs_delta INTEGER NOT NULL,
                PRIMARY KEY(day_utc, asset_kind, asset_id, reason_code, leg_kind)
            );
            CREATE TABLE IF NOT EXISTS mail_delivery_replay (
                realm_id TEXT NOT NULL,
                source_key TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                role_id INTEGER NOT NULL,
                delivery_key TEXT NOT NULL,
                semantic_hash TEXT NOT NULL,
                letter_id TEXT NOT NULL,
                PRIMARY KEY(realm_id, source_key)
            );
            """);
    }

    private static void AddUpgradeColumns(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.AddColumn(db, tx, "operation_receipt", "intent_hash", "TEXT NOT NULL DEFAULT ''");
        SchemaSql.AddColumn(db, tx, "operation_receipt", "intent_payload", "BLOB NOT NULL DEFAULT X''");
        // ReceiptRetention's watermark: every receipt up to this commit sequence has been walked by compaction.
        SchemaSql.AddColumn(db, tx, "economy_meta", "compacted_seq", "INTEGER NOT NULL DEFAULT 0");
        // Up to this commit sequence compacted receipts also lost their child results and owner acknowledgement (#473).
        SchemaSql.AddColumn(db, tx, "economy_meta", "compacted_full_seq", "INTEGER NOT NULL DEFAULT 0");
        // Up to this commit sequence economy_entry rows were aggregated into economy_daily_aggregate and pruned.
        SchemaSql.AddColumn(db, tx, "economy_meta", "pruned_entry_seq", "INTEGER NOT NULL DEFAULT 0");
        // Up to this commit sequence mail/* receipts were walked for mail_delivery_replay extraction and compaction.
        SchemaSql.AddColumn(db, tx, "economy_meta", "compacted_mail_seq", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "economy_entry", "counterparty_owner_kind", "INTEGER");
        SchemaSql.AddColumn(db, tx, "economy_entry", "counterparty_owner_id", "TEXT");
        SchemaSql.AddColumn(db, tx, "economy_entry", "counterparty_compartment_kind", "INTEGER");
        SchemaSql.AddColumn(db, tx, "economy_entry", "counterparty_compartment_key", "TEXT");
        SchemaSql.AddColumn(db, tx, "economy_entry", "counterparty_instance_key", "TEXT");
    }

    private static void RebuildLegacyLegCountConstraint(SqliteConnection db, SqliteTransaction tx)
    {
        // Legacy economy databases used CHECK(leg_count>0); reward, family, and metadata receipts carry zero
        // asset legs (NPC purchases still require legs). Rebuild the constraint while preserving populated tables.
        using var schema = DbSql.Command(db, tx,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='operation_receipt'");
        if ((schema.ExecuteScalar() as string)?.Contains("CHECK(leg_count>0)", StringComparison.Ordinal) != true)
            return;

        SchemaSql.Execute(db, tx, """
            CREATE TEMP TABLE operation_source_cd13b AS
            SELECT realm_id,source_key,operation_id FROM operation_source;
            CREATE TEMP TABLE economy_entry_cd13b AS
            SELECT entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,
                owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,
                delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,
                counterparty_compartment_key,counterparty_instance_key,before_fingerprint,after_fingerprint,source_key
            FROM economy_entry;
            CREATE TABLE operation_receipt_cd13b (
                operation_id TEXT PRIMARY KEY, realm_id TEXT NOT NULL, commit_seq INTEGER NOT NULL UNIQUE,
                actor_account TEXT NOT NULL, actor_role INTEGER NOT NULL, command_kind TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL, intent_hash TEXT NOT NULL, intent_payload BLOB NOT NULL,
                source_key TEXT NOT NULL UNIQUE, recorded_utc_ms INTEGER NOT NULL, effective_utc_ms INTEGER NOT NULL,
                outcome BLOB NOT NULL, child_receipts BLOB NOT NULL, before_revisions TEXT NOT NULL,
                after_revisions TEXT NOT NULL,
                leg_count INTEGER NOT NULL CHECK(leg_count>=0 AND (command_kind<>'npc-shop-buy' OR leg_count>0))
            );
            INSERT INTO operation_receipt_cd13b(operation_id,realm_id,commit_seq,actor_account,actor_role,
                command_kind,request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,
                effective_utc_ms,outcome,child_receipts,before_revisions,after_revisions,leg_count)
            SELECT operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,
                outcome,child_receipts,before_revisions,after_revisions,leg_count FROM operation_receipt;
            DROP TABLE operation_source;
            DROP TABLE economy_entry;
            DROP TABLE operation_receipt;
            ALTER TABLE operation_receipt_cd13b RENAME TO operation_receipt;
            CREATE TABLE operation_source (
                realm_id TEXT NOT NULL, source_key TEXT NOT NULL, operation_id TEXT NOT NULL,
                PRIMARY KEY(realm_id,source_key),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id)
            );
            INSERT INTO operation_source(realm_id,source_key,operation_id)
            SELECT realm_id,source_key,operation_id FROM operation_source_cd13b;
            CREATE TABLE economy_entry (
                entry_id INTEGER PRIMARY KEY AUTOINCREMENT,
                commit_seq INTEGER NOT NULL, operation_id TEXT NOT NULL, leg_no INTEGER NOT NULL,
                recorded_utc_ms INTEGER NOT NULL, effective_utc_ms INTEGER NOT NULL,
                owner_kind INTEGER NOT NULL, owner_id TEXT NOT NULL,
                compartment_kind INTEGER NOT NULL, compartment_key TEXT NOT NULL,
                instance_key TEXT NOT NULL, asset_kind INTEGER NOT NULL, asset_id INTEGER NOT NULL,
                asset_epoch TEXT NOT NULL, delta INTEGER NOT NULL, reason_code INTEGER NOT NULL,
                leg_kind INTEGER NOT NULL, edge_no INTEGER NOT NULL,
                counterparty_owner_kind INTEGER, counterparty_owner_id TEXT, counterparty_compartment_kind INTEGER,
                counterparty_compartment_key TEXT, counterparty_instance_key TEXT,
                before_fingerprint TEXT, after_fingerprint TEXT, source_key TEXT NOT NULL,
                UNIQUE(operation_id,leg_no),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id)
            );
            INSERT INTO economy_entry(entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,
                owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,
                delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,
                counterparty_compartment_key,counterparty_instance_key,before_fingerprint,after_fingerprint,source_key)
            SELECT entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,
                owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,
                delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,
                counterparty_compartment_key,counterparty_instance_key,before_fingerprint,after_fingerprint,source_key
            FROM economy_entry_cd13b;
            CREATE INDEX economy_entry_cut ON economy_entry(commit_seq,entry_id);
            CREATE INDEX operation_source_key ON operation_source(source_key);
            DROP TABLE operation_source_cd13b;
            DROP TABLE economy_entry_cd13b;
            """);
        using var foreignKeys = DbSql.Command(db, tx, "PRAGMA foreign_key_check");
        using var broken = foreignKeys.ExecuteReader();
        if (broken.Read()) throw new InvalidDataException("Receipt constraint migration left a broken foreign key.");
    }
}

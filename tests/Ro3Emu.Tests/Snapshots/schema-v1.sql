-- Frozen SQLite schema for PRAGMA user_version = 1 (baseline commit a9172249).
-- Do NOT edit when adding future migrations: DatabaseMigrationsTests upgrades this
-- frozen v1 fixture through migrations 2..CurrentVersion and checks parity with a fresh DB.

CREATE TABLE account (
    name          TEXT PRIMARY KEY,
    password_hash BLOB NOT NULL,
    salt          BLOB NOT NULL,
    created_utc   TEXT NOT NULL
);
CREATE TABLE account_ban (
    account TEXT NOT NULL PRIMARY KEY,
    reason TEXT NOT NULL DEFAULT '',
    banned_utc INTEGER NOT NULL,
    until_utc INTEGER,
    banned_by TEXT);
CREATE TABLE account_login (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account TEXT NOT NULL,
    utc INTEGER NOT NULL,
    source TEXT NOT NULL,
    address TEXT);
CREATE TABLE account_totp (
    account TEXT PRIMARY KEY,
    secret BLOB NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 0,
    last_step INTEGER NOT NULL DEFAULT 0,
    recovery TEXT NOT NULL DEFAULT '',
    created_utc INTEGER NOT NULL);
CREATE TABLE activity_eve_global (
    activity_id INTEGER NOT NULL, cycle_start INTEGER NOT NULL, blob BLOB NOT NULL,
    PRIMARY KEY(activity_id, cycle_start));
CREATE TABLE activity_pre_donation_area (
    activity_id INTEGER NOT NULL, cycle_start INTEGER NOT NULL, area INTEGER NOT NULL, day INTEGER NOT NULL,
    role_id INTEGER NOT NULL, amount INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(activity_id, cycle_start, area, day, role_id));
CREATE TABLE activity_pre_progress (
    activity_id INTEGER NOT NULL, cycle_start INTEGER NOT NULL, area INTEGER NOT NULL,
    score INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(activity_id, cycle_start, area));
CREATE TABLE admin_audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    utc INTEGER NOT NULL,
    admin TEXT NOT NULL,
    action TEXT NOT NULL,
    target TEXT NOT NULL DEFAULT '',
    detail TEXT NOT NULL DEFAULT '');
CREATE TABLE adventure_group (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    state BLOB NOT NULL
);
CREATE TABLE adventure_group_member (
    role_id INTEGER PRIMARY KEY,
    group_id INTEGER NOT NULL REFERENCES adventure_group(id)
);
CREATE TABLE anticheat_exp (
    role_id INTEGER NOT NULL,
    hour INTEGER NOT NULL,
    account TEXT NOT NULL,
    base_exp INTEGER NOT NULL,
    job_exp INTEGER NOT NULL,
    PRIMARY KEY(role_id, hour));
CREATE TABLE anticheat_refusal (
    account TEXT NOT NULL,
    role_id INTEGER NOT NULL,
    kind TEXT NOT NULL,
    hour INTEGER NOT NULL,
    count INTEGER NOT NULL,
    last_utc INTEGER NOT NULL,
    last_detail TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(account, role_id, kind, hour));
CREATE TABLE auction_bonus (
    role_id INTEGER NOT NULL,
    group_key TEXT NOT NULL,
    amount INTEGER NOT NULL,
    claimed INTEGER NOT NULL DEFAULT 0,
    guild_id INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(role_id, group_key)
);
CREATE TABLE auction_item (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_key TEXT NOT NULL UNIQUE,
    state BLOB NOT NULL
, settled INTEGER NOT NULL DEFAULT 0, auction_type INTEGER NOT NULL DEFAULT 0, run_id INTEGER NOT NULL DEFAULT 0, end_time INTEGER NOT NULL DEFAULT 0);
CREATE TABLE auction_prebuy (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    role_id INTEGER NOT NULL,
    auction_type INTEGER NOT NULL,
    src_type INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    quantity INTEGER NOT NULL,
    unit_price INTEGER NOT NULL,
    currency INTEGER NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    processed INTEGER NOT NULL DEFAULT 0,
    requested_id INTEGER NOT NULL DEFAULT 0,
    paused INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE auction_run (
    id INTEGER PRIMARY KEY,
    state BLOB NOT NULL
);
CREATE TABLE auction_source (
    source_key TEXT PRIMARY KEY
);
CREATE TABLE auth_token (
    token       TEXT PRIMARY KEY,
    kind        TEXT NOT NULL,
    account     TEXT NOT NULL,
    expires_utc INTEGER NOT NULL
);
CREATE TABLE blacklist (owner INTEGER NOT NULL, other INTEGER NOT NULL, PRIMARY KEY (owner, other));
CREATE TABLE casual_battle_obligation (
    source_key TEXT PRIMARY KEY, match_id INTEGER NOT NULL, role_id INTEGER NOT NULL,
    payout BLOB NOT NULL,
    delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
CREATE TABLE dungeon_reward_obligation (
    source_key TEXT PRIMARY KEY, role_id INTEGER NOT NULL, room_id INTEGER NOT NULL,
    activated INTEGER NOT NULL CHECK(activated IN (0,1)),
    completed INTEGER NOT NULL CHECK(completed IN (0,1)), state BLOB NOT NULL);
CREATE TABLE economy_entry (
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
CREATE TABLE economy_meta (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    realm_id TEXT NOT NULL UNIQUE,
    next_sequence INTEGER NOT NULL CHECK(next_sequence>=0),
    retained_floor INTEGER NOT NULL DEFAULT 0 CHECK(retained_floor=0)
, compacted_seq INTEGER NOT NULL DEFAULT 0, compacted_full_seq INTEGER NOT NULL DEFAULT 0);
CREATE TABLE feedback_note (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    report_id INTEGER NOT NULL,
    utc INTEGER NOT NULL,
    author TEXT NOT NULL,
    text TEXT NOT NULL);
CREATE TABLE feedback_triage (
    report_id INTEGER PRIMARY KEY,
    status TEXT NOT NULL,
    assignee TEXT,
    updated_utc INTEGER NOT NULL,
    updated_by TEXT NOT NULL);
CREATE TABLE festival_reward (
    cycle INTEGER NOT NULL, role_id INTEGER NOT NULL, source INTEGER NOT NULL,
    award INTEGER NOT NULL, claimed INTEGER NOT NULL DEFAULT 0, grants BLOB,
    PRIMARY KEY(cycle,role_id,source));
CREATE TABLE festival_round (cycle INTEGER PRIMARY KEY, state BLOB NOT NULL);
CREATE TABLE friend (owner INTEGER NOT NULL, other INTEGER NOT NULL, since INTEGER NOT NULL, intimacy INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (owner, other));
CREATE TABLE friend_apply (target INTEGER NOT NULL, applicant INTEGER NOT NULL, time INTEGER NOT NULL, PRIMARY KEY (target, applicant));
CREATE TABLE friend_chat_group (id INTEGER PRIMARY KEY AUTOINCREMENT, owner INTEGER NOT NULL, name TEXT NOT NULL, created INTEGER NOT NULL);
CREATE TABLE friend_chat_member (group_id INTEGER NOT NULL, role INTEGER NOT NULL, dnd INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (group_id, role), FOREIGN KEY (group_id) REFERENCES friend_chat_group(id));
CREATE TABLE friend_metadata (role INTEGER PRIMARY KEY, data BLOB NOT NULL);
CREATE TABLE gift_code (
    code TEXT NOT NULL PRIMARY KEY COLLATE NOCASE
        CHECK(length(code) BETWEEN 1 AND 64 AND code = trim(code)),
    items TEXT NOT NULL,
    max_uses INTEGER NOT NULL DEFAULT 0 CHECK(max_uses >= 0),
    per_account INTEGER NOT NULL DEFAULT 1 CHECK(per_account >= 1),
    starts_utc INTEGER, expires_utc INTEGER,
    disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1)),
    created_by TEXT,
    created_utc INTEGER NOT NULL DEFAULT (CAST(strftime('%s','now') AS INTEGER)),
    note TEXT);
CREATE TABLE gift_code_redemption (
    redemption_key TEXT PRIMARY KEY, code TEXT NOT NULL, account TEXT NOT NULL,
    role_id INTEGER NOT NULL, redeemed_utc INTEGER NOT NULL);
CREATE TABLE guild (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    state BLOB NOT NULL
);
CREATE TABLE guild_campfire (guild_id INTEGER PRIMARY KEY, state BLOB NOT NULL);
CREATE TABLE guild_caravan_member (role_id INTEGER PRIMARY KEY, state BLOB NOT NULL, revision INTEGER NOT NULL);
CREATE TABLE guild_caravan_version (guild_id INTEGER PRIMARY KEY, day INTEGER NOT NULL, revision INTEGER NOT NULL);
CREATE TABLE guild_challenge (guild_id INTEGER PRIMARY KEY, state BLOB NOT NULL);
CREATE TABLE guild_challenge_clear (
    id TEXT PRIMARY KEY, guild_id INTEGER NOT NULL, season INTEGER NOT NULL,
    cycle INTEGER NOT NULL, difficulty INTEGER NOT NULL, pending INTEGER NOT NULL, state BLOB NOT NULL);
CREATE TABLE guild_event (
    guild_id INTEGER NOT NULL,
    idx INTEGER NOT NULL,
    data BLOB NOT NULL,
    PRIMARY KEY(guild_id, idx)
);
CREATE TABLE guild_hunting (cycle_start INTEGER PRIMARY KEY, data BLOB NOT NULL, runtime BLOB NOT NULL);
CREATE TABLE guild_league(id INTEGER PRIMARY KEY CHECK(id=1),state BLOB NOT NULL);
CREATE TABLE guild_market (
    id INTEGER PRIMARY KEY CHECK(id=1),
    state BLOB NOT NULL
);
CREATE TABLE guild_member (
    role_id INTEGER PRIMARY KEY,
    guild_id INTEGER NOT NULL REFERENCES guild(id)
);
CREATE TABLE guild_recovery (
    guild_id INTEGER PRIMARY KEY,
    state BLOB NOT NULL,
    created_ms INTEGER NOT NULL
);
CREATE TABLE item (
    guid    INTEGER PRIMARY KEY AUTOINCREMENT,
    role_id INTEGER NOT NULL,
    entry   INTEGER NOT NULL,
    stack   INTEGER NOT NULL,
    pack    INTEGER NOT NULL,
    sort    INTEGER NOT NULL,
    grid    INTEGER NOT NULL DEFAULT 0
, attrs TEXT, enchantments TEXT, is_locked INTEGER NOT NULL DEFAULT 0, str_attrs TEXT, bind_type INTEGER NOT NULL DEFAULT 0, bind_expire INTEGER NOT NULL DEFAULT 0);
CREATE TABLE market_record (kind TEXT NOT NULL, id TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY(kind,id));
CREATE TABLE mvp_award_obligation (
    source_key TEXT PRIMARY KEY, cfg_id INTEGER NOT NULL, death_ms INTEGER NOT NULL,
    player_id INTEGER NOT NULL, awards BLOB NOT NULL, result BLOB NOT NULL,
    delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
CREATE TABLE mvp_line_cooldown (cfg_id INTEGER PRIMARY KEY, death_ms INTEGER NOT NULL);
CREATE TABLE mvp_player (
    player_id INTEGER PRIMARY KEY, first_num INTEGER NOT NULL, end_num INTEGER NOT NULL,
    join_num INTEGER NOT NULL, team_num INTEGER NOT NULL, last_time INTEGER NOT NULL);
CREATE TABLE mvp_settlement (
    source_key TEXT PRIMARY KEY, cfg_id INTEGER NOT NULL, death_ms INTEGER NOT NULL);
CREATE TABLE mvp_state (cfg_id INTEGER PRIMARY KEY, state INTEGER NOT NULL, state_time INTEGER NOT NULL, dead_ms TEXT NOT NULL DEFAULT '');
CREATE TABLE operation_receipt (
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
CREATE TABLE operation_source (
    realm_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    PRIMARY KEY(realm_id,source_key),
    FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id)
);
CREATE TABLE owner_operation_receipt (
    operation_id TEXT PRIMARY KEY,
    family TEXT NOT NULL,
    result BLOB NOT NULL
);
CREATE TABLE owner_revision (
    kind TEXT NOT NULL,
    id TEXT NOT NULL,
    part TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision >= 0),
    PRIMARY KEY(kind,id,part)
);
CREATE TABLE panel_news (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    body TEXT NOT NULL DEFAULT '',
    tag TEXT NOT NULL DEFAULT 'news',
    published_utc INTEGER NOT NULL,
    author TEXT NOT NULL DEFAULT '', pinned INTEGER NOT NULL DEFAULT 0);
CREATE TABLE panel_role (
    account TEXT PRIMARY KEY,
    role TEXT NOT NULL,
    granted_by TEXT NOT NULL,
    granted_utc INTEGER NOT NULL);
CREATE TABLE panel_setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE prontera_defense_guild_prep (season_open INTEGER NOT NULL, guild_id INTEGER NOT NULL, exp INTEGER NOT NULL, PRIMARY KEY(season_open,guild_id));
CREATE TABLE prontera_defense_station (season_open INTEGER NOT NULL, guild_id INTEGER NOT NULL, map_id INTEGER NOT NULL, PRIMARY KEY(season_open,guild_id));
CREATE TABLE prontera_defense_world (season_open INTEGER NOT NULL, map_id INTEGER NOT NULL, defense_point INTEGER NOT NULL, PRIMARY KEY(season_open,map_id));
CREATE TABLE recent_team_peer (owner INTEGER NOT NULL, other INTEGER NOT NULL, last_seen INTEGER NOT NULL,
  PRIMARY KEY(owner, other));
CREATE TABLE role (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account         TEXT NOT NULL,
    area            INTEGER NOT NULL,
    name            TEXT NOT NULL UNIQUE COLLATE NOCASE,
    sex             INTEGER NOT NULL,
    career          INTEGER NOT NULL,
    level           INTEGER NOT NULL,
    hair            INTEGER NOT NULL,
    create_time     INTEGER NOT NULL,
    last_login_time INTEGER NOT NULL
, scene_entry INTEGER NOT NULL DEFAULT 10111, pos_x REAL NOT NULL DEFAULT 156, pos_y REAL NOT NULL DEFAULT 0, pos_z REAL NOT NULL DEFAULT 192, face REAL NOT NULL DEFAULT 0, settings BLOB, job_level INTEGER NOT NULL DEFAULT 1, exp INTEGER NOT NULL DEFAULT 0, job_exp INTEGER NOT NULL DEFAULT 0, stats TEXT NOT NULL DEFAULT '1,1,1,1,1,1', bonus_points INTEGER NOT NULL DEFAULT 0, currency TEXT NOT NULL DEFAULT '', starter_kit INTEGER NOT NULL DEFAULT 0, skills TEXT NOT NULL DEFAULT '', skill_bar TEXT NOT NULL DEFAULT '', paragon_level INTEGER NOT NULL DEFAULT 0, paragon_exp INTEGER NOT NULL DEFAULT 0, skill_plans BLOB, tasks BLOB, task_runtime BLOB, task_followup BLOB, guides BLOB, emoji_play BLOB, achievements BLOB, achievement_runtime BLOB, adventure_group BLOB, library BLOB, card_plans BLOB, shop BLOB, trade_info BLOB, stalls_info BLOB, mailbox BLOB, auction BLOB, guild_info BLOB, guild_challenge BLOB, pet_books BLOB, daily BLOB, new_activities BLOB, activity_runtime BLOB, timed_activity BLOB, timed_battle BLOB, training BLOB, lottery BLOB, tower BLOB, wild_afk BLOB, dungeons BLOB, territory_wars BLOB, territory_escort BLOB, lifejob BLOB, life_restrict BLOB, life_runtime BLOB, season_talent BLOB, season_page BLOB, season_page_meta BLOB, season_refine BLOB, equip_levels BLOB, equip_plans BLOB, auto_decompose BLOB, bag_state BLOB, stat_plans BLOB, world_state BLOB, emblems BLOB, pets BLOB, mounts BLOB, wardrobe BLOB, totems BLOB, totem_refine_epoch BLOB, genre BLOB, career_special BLOB, heroic_spirit BLOB, paragon BLOB, title BLOB, area_concur BLOB, growth_class BLOB, first_heart BLOB, personal BLOB, personal_upvote BLOB, popularity BLOB, social_outfit BLOB, emoji_info BLOB, gifts BLOB, barroom BLOB, deleted_time INTEGER NOT NULL DEFAULT 0, gm_state BLOB, job_exp_catchup BLOB, offline_buffs BLOB);
CREATE TABLE role_lifecycle (
    role_id INTEGER PRIMARY KEY,
    epoch INTEGER NOT NULL CHECK(epoch > 0),
    deleted INTEGER NOT NULL CHECK(deleted IN (0,1))
);
CREATE TABLE server_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE territory_escort(id INTEGER PRIMARY KEY CHECK(id=1),state BLOB NOT NULL);
CREATE TABLE territory_wars(id INTEGER PRIMARY KEY CHECK(id=1),state BLOB NOT NULL);
CREATE TABLE territory_wars_seasons(epoch TEXT PRIMARY KEY,state BLOB NOT NULL);
CREATE TABLE timed_battle_obligation (
    source_key TEXT PRIMARY KEY, match_id INTEGER NOT NULL, entry INTEGER NOT NULL,
    role_id INTEGER NOT NULL, grants BLOB NOT NULL, costs BLOB NOT NULL,
    next_state BLOB NOT NULL, result BLOB NOT NULL,
    delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
CREATE TABLE tower_first_pass (floor INTEGER PRIMARY KEY, record BLOB NOT NULL);
CREATE INDEX account_login_account ON account_login(account, id);
CREATE INDEX account_name_nocase ON account(name COLLATE NOCASE);
CREATE INDEX adventure_group_member_group ON adventure_group_member(group_id);
CREATE INDEX anticheat_exp_hour ON anticheat_exp(hour);
CREATE INDEX anticheat_refusal_hour ON anticheat_refusal(hour);
CREATE INDEX auction_item_open ON auction_item(settled, auction_type);
CREATE INDEX auction_item_run ON auction_item(run_id);
CREATE INDEX auction_prebuy_role ON auction_prebuy(role_id);
CREATE INDEX casual_battle_pending_role
    ON casual_battle_obligation(role_id,delivered);
CREATE INDEX challenge_clear_occurrence ON guild_challenge_clear(guild_id,season,cycle,difficulty);
CREATE INDEX challenge_clear_pending ON guild_challenge_clear(pending) WHERE pending=1;
CREATE INDEX dungeon_obligation_pending_role
    ON dungeon_reward_obligation(role_id) WHERE completed=0 AND activated=1;
CREATE INDEX dungeon_obligation_pending_room
    ON dungeon_reward_obligation(room_id) WHERE completed=0 AND activated=1;
CREATE INDEX economy_entry_cut ON economy_entry(commit_seq,entry_id);
CREATE INDEX economy_entry_recorded ON economy_entry(recorded_utc_ms);
CREATE INDEX feedback_note_report ON feedback_note(report_id, id);
CREATE INDEX gift_code_redemption_code ON gift_code_redemption(code,account);
CREATE INDEX guild_member_guild ON guild_member(guild_id);
CREATE INDEX item_role ON item(role_id);
CREATE INDEX mvp_award_pending_player ON mvp_award_obligation(player_id,delivered);
CREATE INDEX operation_source_key ON operation_source(source_key);
CREATE INDEX role_account ON role(account);
CREATE INDEX timed_battle_pending_role
    ON timed_battle_obligation(role_id,delivered);

PRAGMA user_version = 1;

using System.Globalization;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>GmOpEnum values of the character, gear, quest, dungeon, guild, mail, position and server ops.</summary>
    internal const int OpStrengthen = 4, OpRefine = 5, OpResetAttrPnt = 9, OpDropPool = 26, OpGvgReset = 51, OpCreateMultiRole = 54,
        OpSetServerTime = 62, OpSetServerOpenDay = 63, OpDumpServerLevelInfo = 64, OpSendMailPerson = 70, OpSendMailSystemByCfgId = 71,
        OpSendMailSystemByCustom = 72, OpRevokeSystemMail = 73, OpShowLocation = 79, OpFlyToTarget = 83, OpDungeonCmd = 93, OpGuild = 94,
        OpTask = 99, OpEnterDungeon = 108, OpAllSkillsMax = 110, OpPrintBarroom = TavernService.GmPrint, OpSetBarroomInfo = TavernService.GmSetInfo,
        OpAddBarroomToDayStayTimes = TavernService.GmAddStay, OpSetMoveVerify = 186, OpSetRoleAttr = 301;

    /// <summary>GmTask's state option (GMConfig 1021 _kOptionName): accept, abandon, reset, submit, complete, delete.</summary>
    private const int TaskAccept = 0, TaskAbandon = 1, TaskReset = 2, TaskSubmit = 3, TaskComplete = 4, TaskDelete = 5;

    /// <summary>The guild function (FunctionConfig 13) GuildService checks before a creation.</summary>
    internal const int GuildFunction = 13;

    /// <summary>GM panel character, quest, position and server ops that only need GmCommands's core dependencies; null when not handled here.</summary>
    private async Task<string?> CharacterOpAsync(GameSession s, int op, string[] args)
    {
        switch (op)
        {
            case OpResetAttrPnt:
                // (inferred) The current stat plan, as the client's own reset (31501 without a plan index).
                await character.ResetStatsAsync(s);
                return "Stat points of the current plan reset.";
            case OpSetRoleAttr:
                return SetRoleAttr(args);
            case OpTask:
                return await TaskAsync(s, I(args[0]), L(args[1]));
            case OpRevokeSystemMail:
                return $"GM op {OpLabel(op)}: the emulator keeps no record of server-wide mail to revoke (each letter lives only in its mailbox).";
            case OpShowLocation:
                string instance = s.InstanceId != 0 ? $" instance {s.InstanceId}" : "";
                return string.Create(CultureInfo.InvariantCulture, $"Scene {s.SceneEntry} {data.MapName(s.SceneEntry)} line {s.LineNumber}{instance} "
                    + $"at {s.Position.X:F1} {s.Position.Y:F1} {s.Position.Z:F1} face {s.Face:F0}");
            case OpGvgReset:
                return await GvgAsync(s, Arg(args, 0));
            case OpDumpServerLevelInfo:
                int? day = character.ServerDay?.Invoke();
                return $"Server day {day?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, server level {character.ServerLevel()}, "
                    + $"level cap {character.MaxLevel} (no day-based cap: owner decision, all content open).";
            case OpSetServerTime:
                return $"GM op {OpLabel(op)}: the emulator runs on the host clock; one shared server time for every session cannot be set from the panel.";
            case OpSetServerOpenDay:
                return $"GM op {OpLabel(op)}: the open day counts from the season start (SeasonService), which the panel does not set; see !seasonnext.";
            case OpCreateMultiRole:
                return $"GM op {OpLabel(op)}: the emulator creates roles only through an account's own login, not in batches.";
            case OpSetMoveVerify:
                return $"GM op {OpLabel(op)}: movement verification is the host's ServerOptions.WalkCheck ('{options.Value.WalkCheck}'), not a per-role switch.";
            default:
                return null;
        }
    }

    /// <summary>The panel's parameter <paramref name="index"/>, or "" when it was left empty or not sent.</summary>
    internal static string Arg(string[] args, int index) => index < args.Length ? args[index] : "";

    /// <summary>
    /// GmSetRoleAttr [school, level, paid level, has BD] (GMConfig 1033) applies a GMRoleAttrSet row (genre * 1000 + level),
    /// and the client export holds those rows' keys only: their columns are server-side, so there is nothing to apply.
    /// </summary>
    private string SetRoleAttr(string[] args)
    {
        long key = L(args[0]) * 1000 + L(args[1]);
        return data.Row("GMRoleAttrSet", key) is null
            ? $"No GMRoleAttrSet row {key} (school {args[0]}, level {args[1]})."
            : $"GM op {OpLabel(OpSetRoleAttr)}: GMRoleAttrSet {key} has no columns in the client export (a server-side table), so the attribute set is unknown.";
    }

    /// <summary>
    /// GmTask [state, task id] (GMConfig 1021; presets: "4" opens a system by completing its quest) through the !quest
    /// paths. (inferred) Abandon and delete both drop the quest, reset drops and accepts it again, submit hands in only a
    /// running one, complete accepts it when needed and hands it in.
    /// </summary>
    private async Task<string> TaskAsync(GameSession s, int state, long id)
    {
        if (data.Row("TaskConfig", id) is null) return $"No quest {id}.";
        switch (state)
        {
            case TaskAccept:
                return await tasks.GmAsync(s, "accept", id);
            case TaskAbandon or TaskDelete:
                return await tasks.GmAsync(s, "drop", id);
            case TaskReset:
                await tasks.GmAsync(s, "drop", id);
                return await tasks.GmAsync(s, "accept", id);
            case TaskSubmit when s.Tasks?.Infos.GetValueOrDefault(id)?.Status is not (TaskState.Running or TaskState.Finish):
                return $"Quest {id} is not running.";
            case TaskSubmit or TaskComplete:
                return await tasks.GmAsync(s, "done", id);
            default:
                throw new ArgumentException("state 0 accept, 1 abandon, 2 reset, 3 submit, 4 complete, 5 delete");
        }
    }

    /// <summary>
    /// GmGVGReset ["123" = start in 1 s, 2 s preparation, 3 s battle | open | next] (GMConfig 3004). The emulator's siege
    /// test aid (!siege) opens and closes the war now; it has no stage skip or custom timings.
    /// </summary>
    private async Task<string> GvgAsync(GameSession s, string arg)
    {
        string sub = arg.ToLowerInvariant();
        if (sub is not ("open" or "close" or "state"))
            return $"GmGVGReset {arg}: the siege runs on its own timeline; the panel maps only open (and !siege close|state).";
        // !siege takes the event owner (IsEventOwnerCommand), so it runs as its own GM line and answers with a notice.
        await RunAsync(s, "!siege " + sub);
        return $"Siege {sub} requested (test aid !siege {sub}); the result follows as a notice.";
    }
}

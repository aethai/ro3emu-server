using System.Collections.Frozen;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Final attribute values (property data) and the pre-percentage part the client previews from (baseData).</summary>
public sealed class StatSheet
{
    public StatSheet() : this([], []) { }

    internal StatSheet(int capacity) : this(new Dictionary<int, long>(capacity), new Dictionary<int, long>(capacity)) { }

    private StatSheet(Dictionary<int, long> data, Dictionary<int, long> @base)
    {
        Data = data;
        Base = @base;
    }

    public Dictionary<int, long> Data { get; }
    public Dictionary<int, long> Base { get; }

    public long this[int id]
    {
        get => Data.GetValueOrDefault(id);
        set => Data[id] = value;
    }

    /// <summary>An independent copy: callers may change what <see cref="CharacterService.Stats"/> hands them.</summary>
    public StatSheet Clone() => new(new Dictionary<int, long>(Data), new Dictionary<int, long>(Base));
}

/// <summary>
/// The client's attribute pipeline (LC_DAL_PlayerInfo._refreshFinalLocalVal): every attribute
/// belongs to the group of its AttributeConfig._iPreAttri root and has a _iCalculateType (1 Base, 2 BasePerAdd,
/// 3 BuffValueAdd, 4 BuffPerAdd, 6 OtherPerAdd). Values are scaled by _iDataType (1: /10000, 2: /100) and summed per
/// type, then <c>final = B1 * (1 + B2 + B6) * (1 + B4) + B3</c> without intermediate rounding; integer attributes are
/// rounded up like the client's display (Mathf.Ceil).
/// </summary>
public static class StatCalculator
{
    public static StatSheet Compute(IReadOnlyDictionary<int, double> raw, GameData data)
    {
        var metas = Metas(data);
        var sheet = new StatSheet(raw.Count + 8);
        var groups = new Dictionary<int, double[]>();
        foreach (var (id, value) in raw)
        {
            int root = metas.TryGetValue(id, out var meta) ? meta.Root : 0;
            if (root == 0)
            {
                // Not part of a group (client-derived values such as 117-119): sent as is.
                sheet.Data[id] = (long)Math.Round(value);
                sheet.Base[id] = (long)Math.Floor(value);
                continue;
            }
            if (!groups.TryGetValue(root, out var b)) groups[root] = b = new double[7];
            b[meta.Calc] += Scale(meta.DataType, value);
            if (id != root)
            {
                // Members (per_atk 1031 ...) travel as their own properties so the client's preview can redo the group.
                sheet.Data[id] = (long)Math.Floor(value);
                sheet.Base[id] = (long)Math.Floor(value);
            }
        }
        foreach (var (root, b) in groups)
        {
            double final = b[1] * (1 + b[2] + b[6]) * (1 + b[4]) + b[3];
            int dt = metas.TryGetValue(root, out var rootMeta) ? rootMeta.DataType : 0;
            sheet.Data[root] = dt == 0 ? (long)Math.Ceiling(final - 1e-6) : (long)Math.Round(Unscale(dt, final));
            sheet.Base[root] = (long)Math.Floor(raw.GetValueOrDefault(root));
        }
        return sheet;
    }

    /// <summary>One AttributeConfig row as the pipeline reads it: _iPreAttri, _iCalculateType (anything but 1-4 and 6
    /// counts as 1) and _iDataType.</summary>
    private readonly record struct Meta(int Root, int Calc, int DataType);

    // A stat build computes several sheets and read three fields of every attribute's row each time.
    private static FrozenDictionary<int, Meta> Metas(GameData data) => data.Derived("StatCalculator.Metas", () =>
    {
        var metas = new Dictionary<int, Meta>();
        foreach (var (key, row) in data.Table("AttributeConfig"))
        {
            if (!int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id)
                || data.Row("AttributeConfig", id) is not { } own || !own.Equals(row)) continue;
            int calc = (int)GameData.Int(row, "_iCalculateType");
            if (calc is < 1 or > 6 or 5) calc = 1;
            metas[id] = new Meta((int)GameData.Int(row, "_iPreAttri"), calc, (int)GameData.Int(row, "_iDataType"));
        }
        return metas.ToFrozenDictionary();
    });

    private static double Scale(int dt, double v) => dt switch { 1 => v / 10000, 2 => v / 100, _ => v };

    private static double Unscale(int dt, double v) => dt switch { 1 => v * 10000, 2 => v * 100, _ => v };
}

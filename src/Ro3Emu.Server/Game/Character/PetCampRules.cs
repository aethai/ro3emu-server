using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The pet faction (camp) bonus. Deploying PublicParamConfig.PetCampLimitNum pets of one PetBaseConfig._iCamp activates
/// that camp: Lua_CFG_PetBaseConfig.GetActivateCampByData (lua:342-393, read by LC_PetHelper.GetActivateCamp over the
/// set-up pets) counts the camps of the lineup and picks one with at least 3 (the client's literal, equal to
/// PetCampLimitNum and to the pet guide's "Deploying 3 pets from the same Faction activates that Faction's effect").
/// LC_PetHelper.GetPetAttributeInfo (lua:1170-1200) then scales a deployed (enum_PetSetUpState.SetUp) pet of that camp:
/// each attribute that is the _iPreAttri of a PetCampDestinyAttribute [attribute, value] entry is multiplied by
/// (10000 + value) / 10000, before UI_Sys_Pet_LineUpWidgetCtrl's lineup total applies the slot addition. The client
/// reads PetCampDestinyAttribute for every camp (PetCampGrow/Order/PerceptionAttribute have no reader); support pets
/// and pets of other camps are not scaled.
/// </summary>
internal static class PetCampRules
{
    /// <summary>The active camp of a lineup, 0 (enum_PetCamp.None) when no camp has enough pets. With more camps above
    /// the limit, the lowest wins (inferred: the client takes the first of an unordered pairs walk, which four lineup
    /// slots and a limit of 3 never reach).</summary>
    internal static long ActiveCamp(GameData data, IEnumerable<int> setUpPets)
    {
        if (PublicParams.Of(data).PetCampLimitNum is not { } limit) return 0;
        return setUpPets.Where(id => id != 0).Select(id => PetBaseConfig.Get(data, id)?.Camp ?? 0).Where(camp => camp != 0)
            .GroupBy(camp => camp).Where(g => g.Count() >= limit).Select(g => g.Key).DefaultIfEmpty().Min();
    }

    /// <summary>The factor a deployed pet of the active camp applies to one of its attributes (1 for the others).</summary>
    internal static double Scale(GameData data, long attribute)
    {
        double scale = 1;
        foreach (var entry in PublicParams.Of(data).PetCampDestinyAttribute ?? [])
            if (entry is [var id, var value] && AttributeConfig.Get(data, id)?.PreAttri == attribute)
                scale *= (10000 + value) / 10000.0;
        return scale;
    }
}

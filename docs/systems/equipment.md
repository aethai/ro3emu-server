# Equipment grids

`31400 EquipItemOpr` places equipment using `ItemConfig._iEquipPart`; the selected grid is persisted in the active
equipment plan and is also used by plan switching and weapon-card sockets.

A one-handed weapon (`ItemConfig._iEquipPart = 2`, MainHand) may use both weapon grids when the career wields one in
each hand. The client decides it from `JobConfig._kWeaponForm` (`LC_EquipmentHelper.GetWeaponStatusType`, enum_WeaponFromType
SingleHand 1, TwoHand 2, DoubleHand 3): a career without SingleHand but with DoubleHand is `TwoHandAndDoubleHand` and
puts a MainHand weapon in the off hand once the main hand is taken. Today that is the Assassin line, Assassin `5200`,
Assassin Cross `5300` and Guillotine Cross `5400` (`[2,3]`). The client asks for no skill, so neither does the server.

Equipment-plan validation and weapon-card sockets use the same second-hand rule, so saved plans and cards remain valid
when an Assassin equips a dagger in each hand.

## Who may wear a piece

The client's `LC_ItemHelper.CanUseByInfo` decides it, in this order:

1. `ItemConfig._iLevelNeed` up to the role's base level (657 "Wrong Level"). The 544 pieces of EquipConfig ranks 8-15 ask
   level 999, so nobody wears them.
2. `_kJobNeed` empty, or holding 0 or the role's career (655 "Incorrect equipment class").
3. `_iSexNeed` 0 or the role's sex (656 "Incorrect equipment gender"). Every row has 0 today.

The server checks it where the client's `CanEquipToGrid` does, after the appraisal and before the grid:

- **31400 equip.** Refused with the code above.
- **31413 plan switch and the Genre composites.** The gear coming in is checked, so a role can always switch away from
  gear it can no longer wear.
- **Login.** Gear worn against the rules goes to the bag (owner, 27 Nov 2026). It was put on before the server checked, or
  a job change made it so. The move goes through even past the bag's capacity rather than lose the piece. The current
  plan forgets it; other plans keep it, and 31413 refuses them until the role qualifies.

GM preset op 173 goes through 31400 too, so the two client presets whose weapon their level or job may not wear (5100,
1310) leave it in the bag.

A two-handed weapon (`_iEquipPart = 1`) is worn in grid 1 only, but the client lists it in both weapon grids of a plan:
`LC_SchemeMatchHelper.PutOnEquip` writes its guid into `itemsEquipGrid` 1 and 2 (the Genre plans other players build),
`PutOffEquip` clears both, and the 31400 take-on does the same in `EquipGearDataMap`. A plan (31413, Genre staging and
adoption) therefore reads the grid 2 repeat of grid 1's two-hander as the same weapon. Its grid 2 card sockets stay closed:
`LC_CardViewHelper.IsActive` never activates grid 2 beside a two-hander and the card window moves such a slot to grid 1
(`UI_Sys_CardPanelModel.GetPartSlotByGrid`). The refine and strengthen resonance counts grid levels, not worn items, so a
two-hander changes nothing there.

A card stays in its grid's socket when the gear leaves, across relogs too, but it only counts while the client calls that
socket active (`EquipmentStunts.ActiveCards`, `LC_CardViewHelper.IsActive`): a worn item on the grid, always for the costume
grids 8-10, and for the weapon grids by the career's weapon form and the weapon's part (one dagger on an Assassin opens
sockets 1-2, two open 1-4, a single-hand weapon on other careers opens 1-3, the off-hand grid of an Assassin none). Any other
socket shows `enum_CardStatus.NotEffect` ("Inactive", UIWords 30121/30122). Leaving it out of the stats, passives and card
skills is (inferred) from that display. Power and the "socket N cards" objectives still count every socketed card.

Card: Baphomet and Shadow Card: Baphomet (BuffConfig groups 20509, 20508) splash a melee normal attack that hit: up to
`_iTargetMax` other monsters around the locked target take a share of that hit, as an extra cast of the buff's "Splash -
Card" row (`CardEffects.SplashOf`, 31101 and the row's gfx in 31119), which triggers nothing further. Radius and gfx are
the row's `_kLogicData`, the share is `_kDamageParamExtra`, and chance and cooldown are the buff's. That the share is of the
hit's final damage, with no defense of its own, is (inferred) from the text and from the row having no damage ratio.

The other card procs follow the same path (`CardEffects`). An own cast that reached a monster sets off the autocasts, Bongun's
Bash and Wind Ghost's Jupitel Thunder on any `_iDmgType` 1 row and Side Winder's Double Attack on a normal attack, each with the
tier's summed chance and internal cooldown; a heal sets off Pharaoh's Divine Light; a skill with a running cooldown can have
it reset by Mistress. These hang on `CastResolved` and cast extra, so a proc starts nothing further. Family cards (Orc Lady,
Kobold Leader, Goblin Leader, Aster, Crab, Shellfish) raise damage on monsters holding `NPCConfig._kNpcTag` 1000-1005, and
only those: the link from card to tag is (inferred) from who holds the tags. Card: Doppelganger raises the first hit within 2 s
of a crit, and Card: Osiris at T2 and T3 puts on row 2090401 when the revive is taken. Attributes 30 and 31 count through
`AttributeConversionConfig` on 312-320 and the crowd-control resists, from any source.

On-hit cards answer a hit on a monster or a player: status on hit, Hunter Fly, Dracula, Bongun, Wind Ghost, Side Winder and
Doppelganger. A status on a player meets the target's tag resistance. Dark Priest's SP burn acts only on a player, as a monster
has no current SP. A fixed hit (a reflect, a guardian's) sets off none of them. Blind on a monster limits its sight to
`BuffService.BlindRadiusCm` from any source; on a player only a guardian's does.

Cards on the player taking a hit. Armor element cards (group 9044, Angeling's 20515, Shadow Angeling's timed 904407) set the
element `CombatService.ArmorElement` gives EleConfig and 31101 `DstElement`. Dokebi counts as Wind, as its text and 904403
say, though the row named after the card says 3. Golden Thief Bug shows a magic hit as 0 at its chance
(`PlayerDamageAdjusters`, before any share, shield or lethal rule); a tick, a fixed amount and a raid mechanic's share of maximum
HP bypass it, the last being (inferred) what "Boss instant-kill skills" means. Ladybug takes its share off any damage while Silenced.
Maya and Orc Lord reflect a player's skill tagged ST (`_kSkilltag` 102) of their kind through `ReactToHitAsync`, as a fixed hit
of the `Rebound_Card` row the buff names; (inferred) a Basic Attack is no skill, 60% caps the whole share, and a reflect can
kill. Orc Hero refuses category 13, Moonlight Flower category 33, and Eddga knockback and knock-up, each by its group in
`BuffService.Control`, since those rows carry only text.

Phen keeps a cast through a control on the server and adds 1335 to the variable chant (the group formula would give it nothing).
The forbid bits still go out, so whether the client keeps playing the skill is open. The granted Magnum Break's buff makes Basic
Attacks Fire. Envenom, the class skill and Poison Spore's, follows its hit with the row its `_kLogicData` names. Maya Purple and the
indestructible gear cards have no effect possible: no NPC or AI row holds a hiding buff or skill, and nothing breaks gear. Card:
Kraken's Bleeding has no data.

Hunting Mastery 231 and Protection 232 are factors of x / (x + 10000) against monsters. Bonus ATK and DEF 221-224, the Flash
copies' dead line until now, add after defense and before the factors, and not on a tick; nothing converts them into ATK or DEF.
Both placements are (inferred) from `PBDubugDamageFormulaEnum`.

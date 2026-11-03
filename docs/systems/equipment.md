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

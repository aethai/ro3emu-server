# Issue #15: profession choice and pet skills

This note records the two separate failures reported in
[#15](https://github.com/aethai/ro3emu-server/issues/15). The selection fix does **not**
close the whole issue: profession combat effects and ordinary pet skill execution
still require implementation and client verification.

## Profession selection

The attached screenshot is the Season Talent profession selection panel, not
`SkillSwitchGenre`. Client references:

- `UI/Skill/Controllers/UI_Sys_Skill_SeasonTalent_SelectPanelCtrl.lua`,
  `_onClickUpgrade`: sends the selected option's ID in `SeasonTalentUpgradeReq`
  (`83550`). It does not purchase each parent separately.
- `Lua_CFG_SeasonTalentConfig.GetTreeDataByTreeID`: filters profession rows using
  the current job's `SeasonTalentEffectConfig` row.
- `Logic/SeasonTalent/LC_SeasonTalentHelper.lua`, `FindSelectedChild`: the selected
  child is the profession node whose current level reaches its configured maximum.
- `Logic/Asset/LC_DAL_SeasonTalent.lua`: overlays the active plan onto the global
  `5050` node view. Complete plan maps must clear the previous selection.
- `enum_SeasonTalentType`: SkillPoint = 5, Profession_L = 6, Profession_M = 7.

For career 1200, the first choice gate is `10102003`. Pierce Enhance is branch
`10102101`, Rapid Pierce is option `10102102`; Swift Sword is branch `10102105`,
and Critical Attack is option `10102106`.

Previously, `83550` checked whether the requested leaf's parent was already at
maximum level. Neither that parent nor the type-5 choice marker could be purchased
through the ordinary attribute-and-cost path. The client therefore received
`6265` (“This talent has not been unlocked”) for a reachable choice.

The selection path now validates the unlocked type-5 gate, job-compatible branch
and option, then commits the entire path in one role transaction. It clears only
the alternate branches/options below that gate in the active plan. An incomplete
branch request, another job's option, or a locked gate is refused.

**Inferred policies:** selecting/replacing a choice has no item cost (these effect
rows have no purchase cost); the gate, branch and option each store level 1;
free choice markers do not count towards later trees' purchased-level thresholds.
The original server's replacement policy is not captured.

Tests cover the real exported graph and a separate, invented graph that runs in
public CI. They exercise initial selection, replacement, job/gate rejection,
unrelated-choice and plan isolation, retry, persistence, relog, and failed-write
rollback. These tests do not establish client rendering or combat-effect parity.

The real-graph test purchases all prerequisites through `83550` before selecting
the leaf; it does not seed talent levels. It sets up the character's quests,
career, level and currency separately. In this export the first choice gate is in
tree `10102`: 40 purchased levels, condition `80000001` (job rank >= 3 and base
level >= 30), then the two preceding ten-level nodes. Merely changing a career
without satisfying these conditions is not sufficient. The patch retains these
configured requirements.

## Pet investigation and remaining work

`PetStarConfig._iProtectSkill` is the ordinary guardian skill. Several rows are
wrappers: for example, `2353301` (`petyinyue`) resolves through `_kLogicData` to
skill group `23534`. Its child `2353401` applies the owner's attack-speed buff.
The wrapper's empty damage/buff arrays alone cannot execute the skill.

`_iPassiveMain` and `_kActiveSkill` also describe tower combat; treating every one
of those rows as an owner's ordinary passive would apply the wrong effects.
See `LC_PetHelper.GetPetAllSkill` and the distinct protect/reactive/active entries.
The existing ordinary stat contributions from `_kFightSkill`, `_kAssistSkill`
and `_kCollectAttr` are separate from triggered guardian/core effects.

The ordinary combat service currently has no guardian skill trigger/runner.
The follower is presentation, while tower pets use a separate combat path.
This confirms an implementation gap, rather than a failed learned-skill save.
Examples show why a generic “cast the configured row” change is insufficient:

- Rocker's guardian child uses `YaoGun` and adaptive attack to heal allies, even
  though its presentation selector is hostile.
- Zherlthsh's guardian child has multiple damage components and a temporary owner
  buff; the presentation attack-point count is not the damage-component count.
- Rare pets' `_iCorePassiveSkill` effects can require dedicated triggers rather
  than static attributes or a permanent buff.

The report does not identify the pet, its star/stage, or the missing passive.
Those details and an ordinary-client reproduction are still needed to validate
the reported pet behavior. No pet combat behavior is changed by this selection fix.

# Issue #15: profession choice and pet skills

This note records the two separate failures reported in
[#15](https://github.com/aethai/ro3emu-server/issues/15). The fixes cover profession
selection and ordinary guardian execution for the explicitly classified skill
families below. This is **not** complete pet/script parity or a reason to close
the whole issue: remaining scripts, triggered core passives and client verification
are still outstanding.

## Required fidelity: 1:1 with the original

The maintainer explicitly requires original behavior, not a plausible substitute.
**The current guardian implementation does not meet that requirement and must
remain unmerged.** Passing regression tests, using client configuration values and
producing a packet the client can display are not proof of original-server parity.
The 23-family coverage count is an implementation count, not a verified-parity count.

Audit of the current implementation:

| Area | Evidence and current gap |
| --- | --- |
| Skill identity and presentation | `PetStarConfig._iProtectSkill`, the type-8/type-41 rows and `LC_Scene_Sync._useExtraSkill` support the configured IDs and the child-plus-`petConfigId` presentation path. These do not establish the server's complete cast policy. |
| Automatic activation | The 250 ms poll, combat-state requirement and wounded-ally-only heal gate are emulator policies. No original execution trace establishes them. |
| Target selection | `BehaviorTree/trees/normalpet.lua` branches on `IsFollowState`, calls `FindTargetGroupWithCamp`/`SortTargetGroup`, or `GetLeaderTarget(0, 2000)`. The recovered action nodes resolve/filter target lists; `CastSkill` selects a target and sets a result on its context. Our current target fallback and owner-first ally ordering do not reproduce that tree. Its invocation context and camp/party filtering must also be traced before porting it. |
| Cooldown and manual requests | The wrapper supplies a configured cooldown value. Its exact start time, sharing rules and which manual requests the original admits have not been compared with the original server. |
| Timing and multi-hit effects | Even where damage components are described, distributing them evenly over the remaining animation time is an assumption. Client presentation attack points alone do not establish authoritative hit timing. |
| Numeric results | Selecting the larger PATK/MATK, rounding heals/shields and using the emulator's shared damage/defense/crit formula have not been verified against original guardian outcomes. Reading the same coefficients is insufficient. |
| Lifecycle and passive effects | Cancellation after plan/equipment/star changes and retaining landed buffs are inferred. Static passive contributions have regression tests, but original equivalence has not been measured; triggered core passives and several secondary effects are missing. |
| Coverage | 23 of 56 guardian-bearing entries have explicit handlers. Neither those 23 nor the four tested basic pets have a completed original-server comparison. |

Before merging the pet portion, resolve these gaps from recovered executable
logic and same-build original-client/server observations. For each relevant pet,
star/stage and combat mode, compare activation conditions, selected recipients,
cast/hit/cooldown times, numeric HP/stat changes, buff duration/stacking, passive
triggers and equipment/scene transitions. Then turn those observations into
regression expectations independent of the proposed implementation. Record
unresolved behavior as unresolved; do not replace it with an arbitrary rule and
label it verified.

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
the leaf; it no longer seeds talent levels. It sets up the character's quests,
career, level and currency separately. In this export the first choice gate is in
tree `10102`: 40 purchased levels, condition `80000001` (job rank >= 3 and base
level >= 30), then the two preceding ten-level nodes. Merely changing a career
without satisfying these conditions is not sufficient. The patch retains these
configured requirements.

## Ordinary guardian execution

`PetStarConfig._iProtectSkill` is the ordinary guardian skill. Several rows are
wrappers: for example, `2353301` (`petyinyue`) resolves through `_kLogicData` to
skill group `23534`. Its child `2353401` applies the owner's attack-speed buff.
The wrapper's empty damage/buff arrays alone cannot execute the skill.

`_iPassiveMain` and `_kActiveSkill` also describe tower combat; treating every one
of those rows as an owner's ordinary passive would apply the wrong effects.
See `LC_PetHelper.GetPetAllSkill` and the distinct protect/reactive/active entries.
The existing ordinary stat contributions from `_kFightSkill`, `_kAssistSkill`
and `_kCollectAttr` are separate from triggered guardian/core effects.

Previously the ordinary combat service had no guardian trigger/runner. The follower
is presentation, while tower pets use a separate combat path. This was an
implementation gap, rather than a failed learned-skill save.

`GuardianPetRules` resolves the exact star/stage's type-8 wrapper to its type-41
child via `_kAutoLearnSkill`, choosing the primary group named by `_kLogicData`.
It refuses ambiguous/unknown scripts. `LC_Scene_Sync._useExtraSkill` specifically
requires the type-41 child and `petConfigId` to create the local guardian model;
`31121` now carries both, with the owner as `srcID`.

Only an owned pet in an unlocked fight slot of the active plan can act. Support
slots supply their configured passive attributes but do not cast guardian skills.
Direct client requests for internal type-41 effects are refused, even if an
invalid saved skill level would otherwise admit them.

The ordinary combat ticker starts eligible automatic guardian skills in combat.
Manual type-8 requests use the same cooldown/admission path. Each extra action
retains its original role, scene generation, attachment, plan, slot and star/stage;
pending effects cannot survive source changes. It does not replace the player's
cast or auto-attack. Damage goes through the existing combat/kill settlement path,
while buffs, finite shields and healing use the corresponding shared services.

**Inferred execution policies:** poll at most every 250 ms while in combat; use
the owner's current monster target or a nearby monster attacking the owner;
automatic heals wait for a wounded ally; allied support prioritizes the owner
then nearby non-hostile players within the configured radius and target cap.
Cooldown comes from the wrapper. Explicit multi-hit components are distributed
over the child's action tail; visual attack-point count does not multiply damage.
Already-applied timed buffs retain their configured duration after unequipping.

Current explicit families (23 of the export's 56 guardian-bearing pet entries):

- Owner support: Choco, Bobo, Goat, Sohee and Evil Nymph.
- Allied support: Tikbalang; healing: Rocker, Fairy, Squidgitte, Alice and Is.
- Direct damage: Wild Rose, Lava Lizard, Zherlthsh, Miguel, XieXinLianRen,
  Goddess of Hope, Observation, Corrupted Wander Man, Nightmare Terror, Alicel
  and Hodremlin.
- Debuff: Marionette; its nonzero parameter is not treated as damage.

Names above describe the selected families, not full emulation of every secondary
script effect. In particular, the shared buff engine does not yet implement all
control/immunity and reactive behaviors. Unknown periodic, displacement and proc
scripts remain refused rather than being treated as generic damage. Miyabi's
control-only guardian is also left unsupported.

Ordinary `_kFightSkill`, `_kAssistSkill` and `_kCollectAttr` passive stat bonuses
already existed. The patch makes lineup bonuses obey slot eligibility, including
invalid persisted slots. Integration tests verify applying and removing deployed
bonuses. The four rare-pet `_iCorePassiveSkill` families need dedicated event
handlers; this patch does not present those as working.

Real-export wire tests exercise Choco's automatic buff without interrupting the
owner, Rocker's allied heal without damaging its presentation target, Wild Rose's
damage, Bobo's finite shield, passive-stat removal, cooldown sharing, and stale
plan/scene rejection. Public-CI tests use invented data to cover child resolution,
concurrent cooldown admission, ownership/slot checks and pending-effect rejection
after plan, equipment, star or scene changes.

# Connect quests

TaskConfig type 19, 53 quests with no pre-task: "Connect Quest - Defeat the MVP Angeling with a Party" (1901000, target
`[106012, 1, 5020001]`), "Team Up to Defeat MVP ..." and the Golden Thief Bug family (1904000, `[107005, 1, 4300001]`).

## What the client gives

- `LC_ConditionDefine` defines neither 106012 nor 107005 (nor 106013, 106014). The target has no client-side rule; the
  Localization text is the whole rule: a kill of that NPC while in a party.
- Every Connect quest carries ActionConfig 116 `UpdateSocialityScore [points]` (`_iEXECHost` 1). Nothing in the client's Lua or config reads a
  sociality score.

## What the server does

| Rule | Source |
| --- | --- |
| A role starts every Connect quest whose targets all have a reader, like the other root quests (`TaskService.RootTasks.cs`) | (inferred) |
| 106012 and 107005 count one for each kill of the target's NPC for every role that dealt damage to it on its map and is in a party, not only the killer | text of the quest; the participant set is the one of 104014 and "in a party" is (inferred) |
| "In a party" is a roster of two or more, offline members included (`TeamService.MembersOf`) | (inferred) |
| The kill counts through both paths, the transactional `StageKillTaskObligations` and `OnKillAsync` | same as 104014 |
| Action 116 does not hold the delivery and keeps no score | (inferred) |
| One log line summarizes a role's Connect quests; a line per quest would push the role's own lines out of a feedback report | `Feedback.ContextLines` |

## Not done

- 106013 "Participate in a battle royale as a party" (1903000) and 106014 "Auto-battle in a party for @{1} minutes"
  (1902000) have no producer, so those two quests are not started.
- Whether the original server started the Connect quests at login or from a screen is not recoverable from the client export.

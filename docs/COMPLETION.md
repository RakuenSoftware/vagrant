# Completion and retail comparison audit

The runtime is not yet certified as a complete retail-equivalent playthrough. This audit distinguishes the checks
that have run from the work they do not establish. The older omissions in FIELD.md include implemented features;
verify each against the current owner before treating that historical list as a backlog.

## Coverage and limits

| Check | What it establishes | What remains unverified |
|---|---|---|
| 385 individual room runs, with failing rooms rerun after fixes | Room loading and entry events complete under each test's starting state | One continuous campaign with earned keys, story flags, inventory, puzzles and boss outcomes |
| Default field contract | Prologue, initial combat, mastery checks, a door traversal and jump | It grants a chain ability and uses mastery fixtures, then stops after that traversal; it is not an unmodified campaign playthrough |
| Fresh New Game campaign checkpoint | Eighteen room entries through the prologue, the Wine Cellar block puzzle, Goodwin and the following Silver Wolf/Bat encounter, using pad controls and earned state; that state is exported through the real save serializer, then resumes through the closed door, Hall of Struggle crate barrier and bowman to room 9,8 | The Minotaur and remaining campaign, workshop parity and save-point menu coverage along the route |
| Save/resume contract | A real disk save resumes in a fresh field; starting weapon DP/PP, permanent stats, HP/MP, inventory, storage, story flags, map visits and options survive, followed by another room change | A complete campaign resumed across application launches, every equipment combination and every save location |
| Focused combat, menus, traps, movement, effects and options checks | Those named paths execute and satisfy their assertions | Exhaustive combat outcomes, every enemy encounter and every menu layout |
| Final story event through ending | The event hands off to credits, the closing screen accepts input and the game returns to title | Reaching that event through a continuous campaign |
| Title attract demonstrations | Both retail demo starts run to completion without input, alternate by title visit, accept L1 interruption and return to the title flow; headless and rendered checks pass | Full screenshot-by-screenshot comparison against a retail title loop |
| Retail-screen pixel contract in LDR/HDR | Five-bit blend arithmetic against a known background | Screenshot and timing parity for every 3D scene, camera and effect |

## Confirmed defects and remaining gaps

- **Starting weapon continuity, fixed in this audit.** Without explicitly equipping a weapon, battle used the
  INITBTL player weapon while the inventory and save kept a separate carried blade. A disk resume reset a worn
  starting weapon from DP 12593 / PP 11 to DP 12600 / PP 0. FieldView now resolves the starting weapon from its
  carried records, using the same assembled-weapon lookup as the equipment menu. The save/resume contract also
  checks that battle and the carried blade agree after JSON round-tripping and further wear.
- **Title attract demos, fixed in this audit.** TitleSequence now runs the retail zone 20/map 0 and zone 2/map 1
  events, with their demo flags, alternating by title visit. The title contract verifies natural completion and
  interruption in both headless and rendered runs.
- **Workshop Repair, fixed in this audit.** SETUP now asks YES/NO at the disc's rows 146/162, repairs every carried
  blade, shield and armour piece in one transaction, and shows the disc's completion message. NO and Cross leave
  equipment unchanged. MENUC 0x8010740c deducts one PP per hundred DP restored, clamps PP at zero, excludes
  accessories and storage, and does not apply the combining-material mask. Headless and rendered checks cover
  equipped and spare equipment, shields, armour, Iron at Work of Art, cancellation and the completion screen.
- **Workshop assembly and combination remain incomplete.** Those paths still use text panels; executing their
  underlying operations does not establish retail layout, animation, confirmation or navigation parity.
- **Climbing probe, fixed in this audit.** With two puzzle blocks stacked in the Wine Cellar, the body collision
  could stop Ashley before the ground climbing probe reached the stack. The ground solver now probes the proposed
  movement position, as retail's 0x800a7818 does. The campaign route can pick up, stack and climb those blocks and
  reach the following room through player controls. A second movement regression reproduced a ground climb
  following the previous jump's direction (61 units north while climbing east); each climb now takes its current
  movement direction. The movement contract covers that history-dependent case.
- **Hidden enemies attacking, fixed in this audit.** The Blackmarket of Wines script hides Sackheim during
  Goodwin's encounter, but the combat planner and body update still included him. Hidden actors now leave the
  active fighter list, release their tokens and do not count as available opponents. The isolated encounter passes
  through normal attacks without consuming recovery items; the campaign driver also asserts that hidden enemies
  remain stationary while Ashley has control.
- **Item-menu weapon attachment, fixed in this audit.** Weapon mounts now keep the skeleton bone's name, as
  the actor's effect anchors already do. A bare bone index was lost when the menu duplicated Ashley's model,
  producing invalid bone attachments. The workshop contract checks the original and copied weapon mounts.
- **Unlocked doors remaining solid, fixed in this audit.** Lock type 0 was skipped as if the door were already
  physically open. Retail 0x80073b9c accepts it; byte 9 starts the room animation, and 0x8008c200 changes collision
  after it finishes. Interaction now follows that sequence, including two-tile and linked leaves, instead of
  hiding a geometry group. The test player plans up to a closed destination door and presses Cross to open it.
- **Particle motion at the camera plane, fixed in this audit.** Motion-aligned sprites now derive their angle
  through the same limited GTE projection as their corners. Godot's unprojection divided by zero when a particle
  crossed the camera plane during the prologue fight.
- **Full campaign verification remains open.** Individual room entry and a directly started final event cannot
  establish the prerequisites, persistent state or routing of a complete playthrough.

## Next verification work

Continue from a fresh New Game in a single world, keeping the inventory and story state actually earned. Disable
the field contract's ability grants and mastery fixtures for that run. Record
the route and checkpoints, exercise saves and reloads along it, and solve the intervening puzzles through their
player controls. Compare the corresponding retail scenes and interfaces at those checkpoints. Keep direct room
starts and injected combat fixtures as focused tests, with their narrower claims.

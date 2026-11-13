# Completion and retail comparison audit

The runtime is not yet certified as a complete retail-equivalent playthrough. This audit distinguishes the checks
that have run from the work they do not establish. The older omissions in FIELD.md include implemented features;
verify each against the current owner before treating that historical list as a backlog.

## Coverage and limits

| Check | What it establishes | What remains unverified |
|---|---|---|
| 385 individual room runs, with failing rooms rerun after fixes | Room loading and entry events complete under each test's starting state | One continuous campaign with earned keys, story flags, inventory, puzzles and boss outcomes |
| Default field contract | Prologue, initial combat, mastery checks, a door traversal and jump | It grants a chain ability and uses mastery fixtures, then stops after that traversal; it is not an unmodified campaign playthrough |
| Save/resume contract | A real disk save resumes in a fresh field; starting weapon DP/PP, permanent stats, HP/MP, inventory, storage, story flags, map visits and options survive, followed by another room change | A complete campaign resumed across application launches, every equipment combination and every save location |
| Focused combat, menus, traps, movement, effects and options checks | Those named paths execute and satisfy their assertions | Exhaustive combat outcomes, every enemy encounter and every menu layout |
| Final story event through ending | The event hands off to credits, the closing screen accepts input and the game returns to title | Reaching that event through a continuous campaign |
| Retail-screen pixel contract in LDR/HDR | Five-bit blend arithmetic against a known background | Screenshot and timing parity for every 3D scene, camera and effect |

## Confirmed defects and remaining gaps

- **Starting weapon continuity, fixed in this audit.** Without explicitly equipping a weapon, battle used the
  INITBTL player weapon while the inventory and save kept a separate carried blade. A disk resume reset a worn
  starting weapon from DP 12593 / PP 11 to DP 12600 / PP 0. FieldView now resolves the starting weapon from its
  carried records, using the same assembled-weapon lookup as the equipment menu. The save/resume contract also
  checks that battle and the carried blade agree after JSON round-tripping and further wear.
- **Title attract demo remains absent.** TitleSequence sends Stage.Attract through Leave and returns to the
  intro. The title contract verifies that loop; it does not verify a retail demo.
- **Workshop presentation remains incomplete.** FieldMenu's ASSEMBLE, REPAIR and COMBINE paths use text panels.
  Executing the operations does not establish retail layout, animation or navigation parity.
- **Full campaign verification remains open.** Individual room entry and a directly started final event cannot
  establish the prerequisites, persistent state or routing of a complete playthrough.

## Next verification work

Continue from a fresh New Game in a single world, keeping the inventory and story state actually earned. Disable
the field contract's ability grants and mastery fixtures for that run. Record
the route and checkpoints, exercise saves and reloads along it, and solve the intervening puzzles through their
player controls. Compare the corresponding retail scenes and interfaces at those checkpoints. Keep direct room
starts and injected combat fixtures as focused tests, with their narrower claims.

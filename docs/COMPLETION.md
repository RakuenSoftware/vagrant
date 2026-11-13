# Completion and retail comparison audit

The runtime is not yet certified as a complete retail-equivalent playthrough. This audit distinguishes the checks
that have run from the work they do not establish. The older omissions in FIELD.md include implemented features;
verify each against the current owner before treating that historical list as a backlog.

## Coverage and limits

| Check | What it establishes | What remains unverified |
|---|---|---|
| 385 historical individual room runs, with failing rooms rerun after fixes | Room loading and entry events under each test's starting state | These preceded the normal-play mode flag fix and omitted mode-specific actors; they need repeating in normal mode, as well as a continuous campaign |
| Default field contract | Prologue, initial combat, mastery checks, a door traversal and jump | It grants a chain ability and uses mastery fixtures, then stops after that traversal; it is not an unmodified campaign playthrough |
| Campaign route checks | Pad-only runs solve the Wine Cellar stack, Hall of Struggle barrier and the two high-exit block puzzles, including breaking a block and carrying the survivors. Historical saved checkpoints used the wrong mode flag and are retired as normal-campaign evidence. A corrected fresh New Game reaches the Wine Cellar and Goodwin; route verification continues | The first Minotaur and remaining normal-mode campaign, workshop assembly/naming parity and save-point menu coverage |
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
- **Workshop Combine, improved in this audit.** SETUP now offers BLADE, SHIELD and ARMOR, component lists with
  material refusals and SET/EQP badges, a result and stat preview, and YES/NO/CANCEL COMBINE. Preview never changes
  inventory or equipment; the same owner computes and commits the result. The first selection is excluded from
  the second list by record identity/content rather than its temporary C# wrapper. Only YES dismantles an assembled
  weapon or unequips worn armour, returning gems and preserving grips. Headless and rendered checks cover cancellation
  and confirmation, with transaction checks for inventory-slot and nested weapons, shields and armour. Exact retail
  animation and every preview layout still need comparison.
- **Workshop assembly and naming remain incomplete.** Assembly still uses a text panel and naming a LineEdit;
  their native component workbench, confirmation and controller keyboard remain to be finished.
- **Normal-play mode flags, fixed in this audit.** Fresh event worlds now initialize flags 1 and 0x11c to one,
  matching TITLE.PRG 0x8007137c/0x80071254. Leaving flag 1 at zero excluded normal-play-only room actors, including
  the first Minotaur. Earlier campaign checkpoints and room sweeps cannot establish normal-mode encounter coverage.
- **Interaction events, fixed in this audit.** Cross now tests type-4 room conditions before doors and chests.
  Condition 8 compares the interaction tile, as BATTLE.PRG 0x80074860/0x800bf258 do. Previously these scripted gates
  never responded. An interaction that starts an event also stops that frame's player movement.
- **Destroyed blocks remaining interactive, fixed in this audit.** The block lookup now excludes destroyed,
  collapsing and falling records. A real route through room 9,12 previously picked an invisible destroyed block
  instead of the survivor; breaking, lifting, stacking and leaving through room 9,17 now passes through pad controls.
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

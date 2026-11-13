# Completion and retail comparison audit

The runtime is not yet certified as a complete retail-equivalent playthrough. This audit distinguishes the checks
that have run from the work they do not establish. The older omissions in FIELD.md include implemented features;
verify each against the current owner before treating that historical list as a backlog.

## Coverage and limits

| Check | What it establishes | What remains unverified |
|---|---|---|
| 385 historical individual room runs, with failing rooms rerun after fixes | Room loading and entry events under each test's starting state | These preceded the normal-play mode flag fix and omitted mode-specific actors; they need repeating in normal mode, as well as a continuous campaign |
| Default field contract | Prologue, initial combat, mastery checks, a door traversal and jump | It grants a chain ability and uses mastery fixtures, then stops after that traversal; it is not an unmodified campaign playthrough |
| Campaign route checks | Pad-only runs solve the Wine Cellar stack, Hall of Struggle barrier and the two high-exit block puzzles, including breaking a block and carrying the survivors; a later run also carries and stacks the Catacombs stones to climb its east exit. Historical saved checkpoints used the wrong mode flag and are retired as normal-campaign evidence. A corrected fresh New Game reaches the Hall of Struggle; its earned state continues through the lever gate, first Minotaur, defeat scene and a saved post-boss checkpoint, then leaves the Wine Cellar, defeats Dullahan and the optional Catacombs ghost, and saves after each | The remaining normal-mode campaign, exact workshop layout/timing parity and save-point menu coverage |
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
- **Workshop assembly and naming, improved in this audit.** The component workbench replaces assembly's text
  panel: blade-first or grip-first selection, material and compatibility refusals, removable components and gems,
  Start/OK confirmation, and YES/NO/Cancel setup. Components stay in the bag until YES. The MENU8 controller
  keyboard replaces the LineEdit, using the disc's 14-by-9 glyph grid and twenty name cells, with caret movement,
  insertion, deletion, backspace and confirmation. Cancel change after assembly preserves the new weapon's blade
  name. Headless and isolated rendered checks cover ownership, cancellation and two gems; naming and disk-resume
  checks cover all twenty cells. Exact retail animation and every stat-preview layout still need comparison.
- **Model-less interaction actors, fixed in this audit.** Template 0x7f records now retain their event slots
  without loading a nonexistent model, matching BATTLE.PRG 0x80077330. Cross tests actor-participant condition 6;
  tile events also test while standing still. The timed lever gate in room 9,11 now runs through pad input,
  including the block climb, lever choice and three-second exit.
- **Deleted battle actors, fixed in this audit.** The Minotaur's defeat event deletes its actor while the field
  still advances battle presentation. Deletion now releases combatant, weapon, target and pending-action
  references before freeing scene nodes, preventing the repeated disposed-node exception.
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
- **Vanishing enemy bodies, fixed in this audit.** A ghost's warp reached invisible drawing mode 8, then the
  hidden-enemy filter removed it from body updates before it could relocate and reappear. Enabled actors mid-warp
  now continue their body update, matching BATTLE.PRG 0x800a29e8/0x800a2a50; script-hidden enemies remain stopped.
  The campaign contract checks that a warp completes within 240 active frames (excluding paused actions and menus); the earned ghost fight and a separate
  Goodwin regression verify warping and script-hidden enemies respectively; the ghost check also passes rendered.
- **Container equipment continuity, fixed in this audit.** The storage filter now resolves the held weapon through
  the equipment owner for inventory-slot and nested weapons alike. Its older test only excluded the starting
  weapon before it had an explicit equipped reference. Storing an unequipped slot weapon also preserves its gems
  while converting it to the box's assembled record. The container contract checks the parts and attached gem
  through storage and retrieval.
- **Event-variable lifetimes, fixed in this audit.** Script starts clear flags 0x10..0x1f and 0xa8; ordinary room
  changes clear 0x20..0x7f, while a disk resume preserves that room state. BATTLE.PRG 0x800bf6dc/0x800bf7dc,
  0x800be98c and 0x800beb9c establish these ranges. Previously the Minotaur's camera selector stayed in flag 60
  throughout later rooms, preventing the Catacombs north encounter from starting. The disk-resume contract checks
  preserved room variables followed by their reset on the next room change.
- **Full campaign verification remains open.** Individual room entry and a directly started final event cannot
  establish the prerequisites, persistent state or routing of a complete playthrough.

## Next verification work

Continue from a fresh New Game in a single world, keeping the inventory and story state actually earned. Disable
the field contract's ability grants and mastery fixtures for that run. Record
the route and checkpoints, exercise saves and reloads along it, and solve the intervening puzzles through their
player controls. Compare the corresponding retail scenes and interfaces at those checkpoints. Keep direct room
starts and injected combat fixtures as focused tests, with their narrower claims.

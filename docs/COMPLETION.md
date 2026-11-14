# Completion and retail comparison audit

The runtime is not yet certified as a complete retail-equivalent playthrough. This audit distinguishes the checks
that have run from the work they do not establish. The older omissions in FIELD.md include implemented features;
verify each against the current owner before treating that historical list as a backlog.

## Coverage and limits

| Check | What it establishes | What remains unverified |
|---|---|---|
| 385 normal-mode individual room checks, repeated after the state-reset, continuing-transition and body-contact fixes; all pass | Room loading and entry events with normal-play actors, plus each room contract's focused checks | Every encounter, condition and puzzle reached through a continuous campaign; full visual comparison |
| Default field contract | Prologue, initial combat, mastery checks, a door traversal and jump | It grants a chain ability and uses mastery fixtures, then stops after that traversal; it is not an unmodified campaign playthrough |
| Campaign route checks | A corrected normal New Game continues through earned checkpoints, with gear, consumables, spells and story state carried by the real serializer. Pad input solves the Wine Cellar and Catacombs stacks, timed mine corridors, moving platforms, Snowfly Forest and both Undercities; defeats include Minotaur, Dullahan, Lizardmen, Golem, Dragon, Duane, the mine bosses, Giant Crab, Earth Dragon, Grissom/Dark Crusader, Rosencrantz, the later mine elementals, Sky Dragon, Harpy, Lich, Nightstalker, Neesa/Tieger, Water Elemental, Ogre Lord, Snow Dragon, Last Crusader and Minotaur Lord. The earned state collects Clematis and Aster, follows the post-battle scenes and enters the Limestone Quarry at story 65, then defeats Water Elemental with earned Spark Fusion and repaired gear, solves both Quarry magnetic puzzles, consumes Eulelia and Melissa, crosses the final Cloudstone, defeats Snow Dragon, solves Tunnel of the Heartless and crosses The Dark Coast, defeats Last Crusader and Minotaur Lord, obtains the Silver Key, solves both Kiltia puzzle rooms, defeats Kali and reaches the Cathedral. Historical checkpoints using the wrong mode flag remain retired as campaign evidence. | The remaining Cathedral campaign and an earned ending; exact workshop layout/timing parity |
| Save/resume contract | A real disk save resumes in a fresh field; starting weapon DP/PP, permanent stats, HP/MP, inventory, storage, story flags, map visits and options survive, followed by another room change. An earned Work of Art state also walks onto its circle, saves through DATA and the card screens, and resumes into another room in a fresh process | A complete campaign resumed across application launches, every equipment combination and every save location |
| Focused combat, menus, traps, movement, effects and options checks | Those named paths execute and satisfy their assertions | Exhaustive combat outcomes, every enemy encounter and every menu layout |
| Final story event through ending | The event hands off to credits and the closing screen accepts input. A separate ending check now observes the actual SceneManager replacement and title menu, headless and rendered | Reaching that event through a continuous campaign |
| Title attract demonstrations | Both retail demo starts run to completion without input, alternate by title visit, accept L1 interruption and return to the title flow; headless and rendered checks pass | Full screenshot-by-screenshot comparison against a retail title loop |
| Retail-screen pixel contract in LDR/HDR | Five-bit blend arithmetic against a known background | Screenshot and timing parity for every 3D scene, camera and effect |

## Confirmed defects and remaining gaps

- **Key and sigil story flags, fixed.** Successful gadget-bag collection now sets the native possession flag; a full bag leaves it unset. Gates inspect owned items before the recorded flag so a collected sigil is consumed and advances to its used state. The focused bag test fails before the change and passes headless and rendered afterward. The earned Aster return now triggers Neesa/Tieger's introduction and victory sequence.
- **Body contact, fixed.** Contact uses the SHP collision radius/height rather than a constant horizontal circle, with native vertical contact, square bounds and movement away from overlap. Separate-floor, boundary, corner and exclusion fixtures pass headless and rendered; pit and Cloudstone checks pass, followed by 385 room checks. One cold-cache room timed out initially and passed on retry.
- **Awaited system-message input, fixed.** A single display-frame Circle/Cross edge could be missed by a 30 Hz event wait. The field now consumes its existing buffered input, clearing earlier edges when the message opens. The missed-phase fixture fails before the change; both display phases and a rendered check pass afterward.

- **Target damage readout, fixed.** Normal targeting showed the limb's remaining HP rather than estimated damage. It now displays the unvaried damage from the same combat preview already used for hit chance, as the [US instruction manual](https://www.gamesdatabase.org/Media/SYSTEM/Sony_Playstation/Manual/formated/Vagrant_Story_-_2000_-_Square_Co.%2C_Ltd..pdf) specifies. The regression fails before the fix; headless and rendered checks verify independence from remaining HP, response to weapon strength, and no committed combat state.

- **Magnetic block repulsion, fixed.** Matching magnetic blocks previously stacked in contact. The crate owner now reads and runs the native fourteen-step repulsion motion, preserving its 128-unit gap on initial placement as well. Opposite colours remain in contact. The focused placement test fails before the fix and passes headless and rendered afterward; seven magnetic-room entry checks and pit, platform and body-contact regressions pass. The earned route stacks the matching blocks, crosses the pit, consumes Eulelia and saves through DATA at The Auction Block in headless and rendered runs.

- **Six-leg Cloudstone paths, fixed.** Four-waypoint platforms decoded only four of their six movement legs. The Quarry platform consequently jumped 472 units on its return. The decoder now preserves all native legs and the initial leg index (BATTLE.PRG 0x8008c9a8/0x8008ca54); no model geometry changes. The focused path test fails before the fix and passes headless and rendered afterward, covering two complete cycles and a nonzero initial leg. All 30 platform-room entry checks and the focused magnet, carrying, platform, path and body-contact checks pass. The pit check also verifies a stable return while permitting normal HP regeneration. Both earned Quarry runs board, ride and jump off the platform into the Snow Dragon approach.

- **Platform-stop exits, fixed.** The Temple exit lift reached its upper stop without dispatching its door. The decoder now preserves its door stop and platform attributes; the existing field owner sets and clears the door bit on arrival and departure and looks up platform-level doors even without horizontal movement (BATTLE.PRG 0x8008b764, 0x8008bb50, 0x8008cc3c). The focused ride test fails before the fix and passes headless and rendered afterward, checking against an early exit. All 37 platform-room and focused movement regressions pass. Both earned campaign runs ride the lift and complete the following town scene.

- **Receiving lift placement, fixed.** The Cathedral transition ignored the destination platform level, placing Ashley in the pit below the lift and repeatedly returning her there. The field now preserves the level, enables and positions the receiving lift at its designated door stop and starts its return leg, placing Ashley on it (BATTLE.PRG 0x8008c674/0x8008c6b4, 0x800a3220). The focused transition fails before the fix and passes headless and rendered afterward; it also checks level 3 selects the second lift and verifies both actual return legs. All 38 platform-room and focused movement regressions pass. Both earned runs cross the repaired transition and defeat Djinn.

- **Enemy-count script query, fixed.** Opcode B7/4 was decoded as an ability query. BATTLE.PRG 0x800bb130 instead calls 0x8007cf80 to count side-4 fighters into flag 0xb1. The decoder and field handler now implement that query. The Malice regression fails before the fix with count zero and passes headless and rendered afterward: three actors on entry, their defeat, the native upper-Cloudstone cutaway and return. All 385 normal-mode room-entry checks pass again after this fix. The earned campaign independently clears the Lich Lord and its escorts, activates the bridge and returns, headless and rendered. The driver can target Skeletons with the already learned Heal spell, whose native undead effect damages them.

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
  the first Minotaur. Earlier campaign checkpoints and room sweeps cannot establish normal-mode encounter coverage; the new 385-room
  sweep passes with these normal-play flags.
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
  preserved room variables followed by their reset on the next room change. The opening field and title/attract
  contracts also pass after this change.
- **Continuing room transitions, fixed in this audit.** Opcode 68 bit 2 preserves the running event across
  a room load instead of starting the destination's entry script. BATTLE.PRG 0x800b99f4, 0x800bea7c and
  0x800b9a14 implement the request/acknowledgement/resume states. Previously Golem's victory event stopped
  during its cutaway, giving the player control in the wrong room before its return sequence. The focused
  round-trip check passes headless and rendered; the earned Golem fight now completes its reward scene,
  returns to the arena with flag 190 at 4 and writes its checkpoint. The Sanctum entrance stack also passes
  headless and rendered checks. These route tests move, interact, attack and recover through pad input.
- **Earned equipment repair and Sanctum continuation, verified in this audit.** The worn blade returns to
  12600/12600 DP through SETUP, spending the PP for its actual wear; the earned repair also passes rendered.
  The return stair route and the entrance's already-solved layout pass their rendered checks. The normal
  lineage then carries the annex stone, climbs its perch, boards and rides its moving platform, defeats Dragon
  and saves with flags 190=5 and 187=8 after the reward event. No encounter HP, items, spells or story flags
  are granted by this driver. Its focused room fixtures remain separate from that normal lineage.
  The Work of Art state also saves through DATA on magic circle 6, passes its rendered card-screen check
  and resumes in a fresh process. The main lineage continues through the town scenes, defeats Duane and
  both companions (story flag 33), and defeats the mines' Wyvern with flag 187=12. The Duane run also passes
  rendered. When its actual RISK roots are exhausted, the driver sheathes and waits for native recovery before
  attacking again; enemy regeneration no longer counts as progress in its combat-stall check.
- **Full campaign verification remains open.** Individual room entry and a directly started final event cannot
  establish the prerequisites, persistent state or routing of a complete playthrough.

The earned Snow Dragon and Last Crusader victories are headless; failed rendered fight attempts remain in the audit record. Minotaur Lord and Silver Key collection pass both headless and rendered. Tunnel of the Heartless and The Dark Coast then pass both headless and rendered using the actual block, jump and ledge controls. The Silver Key branch uses 39 pushes and breaks through normal controls; the return and both Resentful Ones puzzles also pass headless and rendered. A diagnostic reload inside the repeatable Minotaur encounter respawned it, so its victory, chest collection and exit are checked in one continuous segment. Both runs then complete the Kali introduction, defeat Kali and ride the Temple exit lift to Plateia Lumitar, continuing into the Cathedral to defeat Ifrit, Iron Crab, Marid, Djinn, Flame Dragon, Dao and Lich Lord, collect Calla and Palm, advance story 77, activate the Cathedral upper bridge, collect Laurel and save through DATA at Sin and Punishment’s magic circle 33. The combat driver gives critical healing priority over optional preparation. Against the resistant Cathedral guardians it assigns the already learned Phantom Pain and Instill through BATTLE ABILITIES and finishes with Temper to preserve the blade's actual PP. Recovery uses actual consumables and native regeneration. No inventory, HP, position or story-state grants are used in those campaign checks.

## Next verification work

Continue the normal New Game lineage through the Cathedral route, keeping
only its actual inventory and story state. The campaign driver uses pad input and does not apply the default field contract's ability or mastery
fixtures. Most checkpoint files use the real disk serializer while bypassing the save-point menu. Earned checks at Work of
Art and later town, Undercity and mine circles also use the actual circle and DATA screens. Those establish the
checked locations' positive save paths; every save point and full save UI coverage remain open. Record the continuing route and checkpoints, exercise saves and
reloads along it, and solve the intervening puzzles through their player controls. Compare the corresponding
retail scenes and interfaces at those checkpoints. Keep direct room starts and injected combat fixtures as focused tests, with their narrower claims.

# Direct cooperative ropes

Version 2.2.11 uses a dedicated native
`LogicalRope`. Each client attaches its local harness to `RopeSide.End` and the
partner harness to `RopeSide.Begin`, matching the native shared-rope game mode.
The existing pair-link and player-pose packets are unchanged.

## Ownership and lifecycle

The mod creates a fresh logical rope and one native segment from `ropePartPrefab`.
It does not clone a simulated personal rope or its holder list. Initialization
waits for the segment's Obi actor to load and times out after five seconds.
The maximum length is copied from the source rope; when an inactive source has
not initialized its length, the game's shared-rope default is used.

While attached, the cooperative rope becomes the local lifeline's selected rope,
as it does in Cairn's shared-rope mode. The personal rope is retained and selected
temporarily for piton operations, then restored permanently when the cooperative
link ends. A verified partner attachment counts as one belay anchor. Disconnection,
death, missing endpoints, or teleportation releases the cooperative resources. The
E key still detaches an existing partner outside the initial three-metre attachment
radius.

No synthetic Piton enters the placed-piton list, inventory or save data. Old
pitons already saved by earlier versions are left untouched because their origin
cannot reliably be inferred from position or appearance.

## Native evidence

Metadata and targeted disassembly of the installed `GameAssembly.dll` show:

- `Lifeline.AttachToPiton` (RVA `0x30B38A0`) registers an absent piton in
  `PlacedPitons`, then attaches its quickdraw. The previous count-based discovery
  could broadcast this synthetic entry as an ordinary placement.
- `Lifeline.AddPiton(Vector3, Quaternion, PitonExecutionQuality, int,
  InventoryItemStringId, ClimbingV2PawnController)` initializes the placed
  piton's climbing setting through the controller. Remote placements use this
  typed overload instead of creating a null setting and repairing it afterwards.
- `SharedRopeGamemode.AttachClimberToClimber` (RVA `0x3161950`) calls
  `LogicalRope.AttachTo` twice with End/Begin holders, without a piton intermediary.
- `Harness.GetAttachPosition` (RVA `0x2F87670`) reads the physical attach collider,
  which must follow the skeleton when remote physics is inactive.
- `SharedRopeGamemode.Init` (RVA `0x31642B0`) selects its dedicated rope through
  `Lifeline.securingRope`; cleanup clears that selection. This matters beyond the
  initial attachment: `ClimbingV2PawnController.GetFallDistanceBehind` (RVA
  `0x28F8660`) reads `Lifeline.securingRope.GetLength()` after `IsSecured(1)`.
- A `LogicalRope` created with `AddComponent` does not construct `ropeParts`; that
  list normally comes from prefab serialization and must be initialized explicitly.
- The shared-rope attach path queues its final length with `RequestSetLength`, so
  CairnMP does the same instead of mutating Obi length directly outside
  `LogicalRope.FixedUpdate`.

These addresses identify inspected code in this installation, not portable hooks.
Static inspection and managed unit tests do not establish in-game physics quality.

## Two-account acceptance checks

- Use 2.2.11 on host and guest. Attach on flat ground, walk, turn, climb and descend;
  both ends should stay at their harnesses, with no stationary point above them.
- Repeat attachment/detachment ten times. Neither client's placed-piton count nor
  inventory should change. No piton placement packet should be emitted.
- Place and recover real wall pitons before, during and after cooperative use.
  Their positions and personal rope attachments should survive detachment.
- Test a controlled fall and maximum rope tension in a nonlethal test area.
  Verify native suspension and that E still releases the link at zero slack.
- Test a late-loaded remote model, partner departure, death, teleportation,
  checkpoint reload and scene streaming; verify no orphaned rope actors.
- Save/reload, leave the lobby and load solo in the same process, then restart.
  Verify the absence of synthetic pitons and residual movement restrictions.

Runtime acceptance remains pending until these checks are performed with two
distinct Steam accounts. No migration deletes previously saved pitons.

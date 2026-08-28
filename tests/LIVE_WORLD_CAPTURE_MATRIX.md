# Exact capture live-world verification matrix

The automated suite exercises the seam lifecycle, packet `InternalY` reconstruction for a nonzero
dimension, absolute coordinates/dimensions, suppression, generation tracking, exact type/asset
allowlist, actual 1.22.7 microblock/chisel `ToTreeAttributes` canonicalization, immutable append
requests, and PostgreSQL ledger round trips. It does not boot a Vintage Story server world. Before
enabling replay, verify the following on an unmodified Vintage Story 1.22.7 dedicated server:

| Mutation | Before / after | Expected ledger result |
| --- | --- | --- |
| Place ordinary full cube | air / plain `Block` | one `Place` row |
| Replace ordinary full cube | plain `Block` / plain `Block` | one `Place` row |
| Cancelled or no-op place | unchanged | no row |
| Break ordinary full cube | plain `Block` / air, matching `DidBreakBlock` old id | one `Break` row |
| Cancelled break or confirmation mismatch | unchanged or mismatched event | no row |
| Convert ordinary cube with chisel | plain `Block` / exact `game:chiseledblock` + `BlockEntityChisel` | one `ChiselConversion` row |
| Change one chisel voxel | exact chisel BE tree / changed exact chisel BE tree | one `ChiselVoxel` row per changed transition |
| No-op chisel packet | identical BE tree | no row |
| Snow-covered chisel/microblock | exact `game:*block-snow` + matching exact BE | one applicable action row |
| Chisel/microblock with one or more materials | exact BE with registry material IDs | row stores `materials` as exact `domain:path` strings and contains no numeric block registry IDs |
| Chisel/microblock with face decor | BE `decorIds`/`decorRot` present and nonzero | no row; generation still advances for a Changed player seam |
| Chisel/microblock with support beams | BE behavior contains serialized beam block references | no row; generation still advances for a Changed player seam |
| Fluid placement/break or waterlogged coordinate | any fluid-layer state | no row; generation still advances for a Changed player seam |
| Decor/subdecor at coordinate | any decor state | no row; generation still advances for a Changed player seam |
| Chest/container or arbitrary block entity | any non-allowlisted BE | no row; generation still advances for a Changed player seam |
| Modded chisel lookalike | derived/foreign block or BE type/domain | no row; generation still advances for a Changed player seam |
| Scoped suppressed world write | any | no row and no generation change |
| Mod unload/reload | capture capability available | each seam has one handler after reload and none from the disposed instance |

For every stored row, inspect that `dimension`, absolute `x/y/z`, actor UID/name, action kind,
`domain:path` asset codes, and before/after TreeAttribute bytes match the live state. For chisel
payloads, parse the tree and confirm `materials` is a string array, `posy` is the original
`BlockPos.InternalY`, and no `decorIds`, `decorRot`, or `beams` field is present. Also force one
database append failure and confirm a single server-log error occurs without interrupting gameplay.

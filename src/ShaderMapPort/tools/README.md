# Re-deriving the game's frozen layouts

`Layouts.cs` hard-codes Marvel Rivals' frozen layouts. They come from the shipping exe, so a game
patch that touches shader or material classes can move them. These scripts re-derive them
(Python 3.11, `pip install capstone`):

| Script | What it gives |
|---|---|
| `dump_layout_fields.py <exe> <out.json>` | Every `LAYOUT_FIELD` the exe registers at static init: type → (field, offset). Covers most types, not all (some init code stores through registers the scan does not follow). |
| `dump_bind_offsets.py <dir-of-these-scripts> <exe> <func-rva-hex> <len-hex>` | For a `Bind()` function: `this+offset` per parameter name. In a shipping build the in-memory layout is the frozen layout, so this fills the gaps (e.g. `FGPUSkinVertexFactoryShaderParameters` at RVA `0x3c9d7c0`). Find the function from an xref to one of its parameter-name strings (`layoutscan.py`: `find_wstr`, `xrefs`). |
| `frozen_type_table.py <stock.uexp> <game.uexp>` | Side-by-side type table (names, sizes, hash equal/different) of a stock-cooked material and a game material, to see which types a new material needs. |

When `ShaderMapPort` refuses a type ("has no ... layout in this tool"), add it to `Layouts.cs` for
both sides: the stock layout from the UE 5.3 source, the game one from these scripts. Mesh-material
shader types whose own fields match the game go into `Layouts.MeshShaderOwnFields`.

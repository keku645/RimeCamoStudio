using System;
using System.Collections.Generic;

namespace RimeCamoStudio.Project;

/// <summary>What kind of value an external constant of the weapon presets takes — which components matter.</summary>
public enum ConstantKind
{
    /// <summary>One number in x; the graph reads only the first component.</summary>
    Scalar,

    /// <summary>A 2D repeat count in x and y; the graph multiplies the UV by the whole vector.</summary>
    Tiling,

    /// <summary>An RGBA multiplier; the graph multiplies a colour by the whole vector.</summary>
    Color,

    /// <summary>Set by the game at run time, not by any material; a preview value only affects the preview.</summary>
    Engine,
}

/// <summary>
/// One external constant of the presets, as measured.
/// </summary>
/// <param name="VehicleRange">
/// What the game uses for it ON VEHICLES, where that differs from the weapons.
///
/// ⛔ A RANGE IS A MEASUREMENT OF A CONTEXT AND MUST TRAVEL WITH IT. The weapons' camos run WearAmount
/// 10–30; the vehicles' materials run it to 300, and CamoTiling to 11 where a weapon sits at 3. Showing a
/// tank the weapons' numbers is a tranquillising fallback — the user tunes by eye against a range that is
/// not the one they are in. Null = the same range holds for both.
/// </param>
public sealed record WeaponConstant(string Name, ConstantKind Kind, string Title, string What, string Range,
    string? VehicleRange = null);

/// <summary>
/// The twelve external constants every weapon preset carries, and what each one is — the ONE place the
/// simple form's help texts, the notes on the graph's nodes and the single-number expansion read from.
///
/// MEASURED, not guessed: which components each constant feeds comes from the translated graph (a tiling
/// multiplies the UV as x,y; a darkening multiplies a colour as RGBA; the rest read .x), the ranges from the
/// material scan of all 348 weapon meshes, and the extremes from rendering them (WearAmount 0 shows the
/// whole pattern; WearPower 0 removes it, anything to the power 0 being 1; CamoTiling 0 collapses the
/// pattern to one colour). ScopeOcc and PickupSpec appear in no material at all: the game feeds them.
/// </summary>
public static class WeaponConstants
{
    public static readonly IReadOnlyDictionary<string, WeaponConstant> All =
        new Dictionary<string, WeaponConstant>(StringComparer.OrdinalIgnoreCase)
        {
            ["CamoTiling"] = new("CamoTiling", ConstantKind.Tiling, "Tiling",
                "How many times the pattern repeats across the weapon (X along it, Y across it; each its own box). " +
                "0 collapses the pattern to a single colour.",
                "the game's own camos use 3 (0.3–4 seen)",
                "the game's vehicles use 0–11 (185 materials)"),

            ["WearAmount"] = new("WearAmount", ConstantKind.Scalar, "Wear",
                "How much of the pattern is rubbed off: the wear mask (the green channel of the specular " +
                "texture) times this number. 0 shows the whole pattern, pristine; higher rubs more of it off.",
                "the game's own camos use 10–30 (up to 150 seen)",
                "the game's vehicles use 0–300 (331 materials — far higher than a weapon's)"),

            ["WearPower"] = new("WearPower", ConstantKind.Scalar, "Wear edge",
                "How sharp the worn edge is: the wear mask raised to this power. 1 is soft, 5–10 crisp. " +
                "0 removes the pattern entirely (anything to the power 0 is 1).",
                "the game uses 1–10",
                "the game's vehicles use 0–100"),

            ["NormalTiling"] = new("NormalTiling", ConstantKind.Tiling, "Grain tiling",
                "How many times the fine surface grain (the detail normal map) repeats (x and y). " +
                "0 flattens the grain.",
                "the game uses 10–200"),

            ["ScratchTiling"] = new("ScratchTiling", ConstantKind.Tiling, "Scratch tiling",
                "How many times the scratches texture repeats (x and y). 0 removes the scratches.",
                "the game uses 0–25",
                "the game's vehicles use 0–20 (103 materials)"),

            // ⭐ THE VEHICLE PRESETS' OWN, measured over the 1282 materials under vehicles/ — names, shapes
            // and ranges read off the materials, DICE's two typos included (ScatchAmount, DiffuseBrighness:
            // a corrected name matches no parameter and the box would feed nothing).
            ["ScatchAmount"] = new("ScatchAmount", ConstantKind.Scalar, "Scratches",
                "How much of the scratch layer shows through the paint — the vehicles' equivalent of wear for " +
                "the fine scratches. 0 removes them. (The missing 'r' is the game's own spelling.)",
                "the game's vehicles use 10–90 (93 materials)"),

            ["CamoBrightness"] = new("CamoBrightness", ConstantKind.Color, "Pattern brightness",
                "Multiplies the camo pattern on a vehicle, RGBA: 1 leaves it as painted, higher lifts it, " +
                "lower sinks it into the hull. One number is a grey; r,g,b,a tints.",
                "the game's vehicles use 0.46–10 (68 materials)"),

            ["SpecularBrightness"] = new("SpecularBrightness", ConstantKind.Scalar, "Specular brightness",
                "Multiplies the specular the surface reflects — how hard the sun sits on the hull.",
                "the game's vehicles use 0.5–100 (36 materials)"),

            ["DiffuseBrighness"] = new("DiffuseBrighness", ConstantKind.Color, "Diffuse brightness",
                "Multiplies the vehicle's own diffuse texture, RGBA. (The missing 't' is the game's spelling.)",
                "the game's vehicles use 0–40 (10 materials)"),

            ["CamoAOffset"] = new("CamoAOffset", ConstantKind.Color, "Pattern A offset",
                "Slides the first camo layer over the aircraft's skin. Aircraft presets only.",
                "the game's jets use 0.1–1.3"),

            ["CamoBOffset"] = new("CamoBOffset", ConstantKind.Scalar, "Pattern B offset",
                "Slides the second camo layer over the aircraft's skin. Aircraft presets only.",
                "the game's jets use 0.085–0.3 (30 materials)"),

            // (review 2026-09-29: End Game's dirt preset, xp5_vehiclepreset_dirt — its RDEF declares `float3 external_GrimeColor`, fed whole
            // into a subtract and a multiply: a colour. Unknown to this table it expanded one number as x alone — "0.5,0,0,0", a RED grime in
            // the preview — and the bake left it out.)
            ["GrimeColor"] = new("GrimeColor", ConstantKind.Color, "Grime colour",
                "The colour of the grime layer of End Game's vehicles, RGB: one number is a grey, r,g,b a tint. " +
                "End Game dirt preset only.",
                "the game's range is not measured yet (its shader's default is 1,0,0)"),

            ["SmoothnessScale"] = new("SmoothnessScale", ConstantKind.Scalar, "Gloss scale",
                "Multiplies the whole smoothness of the surface. Armored Kill / End Game vehicle presets.",
                "the game's vehicles use 0.5–1"),

            ["SmoothnessMax"] = new("SmoothnessMax", ConstantKind.Scalar, "Gloss ceiling",
                "The highest smoothness the surface is allowed to reach.",
                "the game's vehicles use 0.8"),

            ["SmoothnessRegular"] = new("SmoothnessRegular", ConstantKind.Scalar, "Gloss (bare surface)",
                "Smoothness of the surface outside the paint and the wear: 0 matte, 1 mirror.",
                "the game uses 0.2–0.7"),

            ["SmoothnessMasked"] = new("SmoothnessMasked", ConstantKind.Scalar, "Gloss (painted)",
                "Smoothness where the pattern is painted on: 0 matte, 1 mirror.",
                "the game uses 0.1–1"),

            ["SmoothnessWear"] = new("SmoothnessWear", ConstantKind.Scalar, "Gloss (worn metal)",
                "Smoothness where the paint has worn through to the metal: 0 matte, 1 mirror.",
                "the game uses 0.3–1"),

            ["DiffuseDarkening"] = new("DiffuseDarkening", ConstantKind.Color, "Diffuse tint",
                "Multiplies the weapon's diffuse texture, RGBA: 1 leaves it as painted, 0.5 halves it, 0 is " +
                "black. One number is a grey; r,g,b,a tints.",
                "the game uses 0–1 (0.5 is common)"),

            ["CamoDarkening"] = new("CamoDarkening", ConstantKind.Color, "Pattern tint",
                "Multiplies the camo pattern, RGBA: 1 shows it as painted, lower darkens it, r,g,b tints it.",
                "the game uses 0.16–1"),

            ["ScopeOcc"] = new("ScopeOcc", ConstantKind.Engine, "Scope occlusion",
                "Set by the game while aiming down the scope; it is not a material value. Leave it empty — a " +
                "preview value here only affects the preview.",
                "not in any weapon material"),

            ["PickupSpec"] = new("PickupSpec", ConstantKind.Engine, "Pickup highlight",
                "Set by the game for a weapon lying on the ground; it is not a material value. Leave it " +
                "empty — a preview value here only affects the preview.",
                "not in any weapon material"),
        };

    /// <summary>The constant's entry, by its bare name ("CamoTiling") or its node name ("external_CamoTiling").</summary>
    public static WeaponConstant? Find(string p_Name)
    {
        if (p_Name.StartsWith("external_", StringComparison.OrdinalIgnoreCase))
            p_Name = p_Name["external_".Length..];

        return All.TryGetValue(p_Name, out var s_Constant) ? s_Constant : null;
    }

    /// <summary>
    /// The four-component vector ONE number stands for on a constant — what the user types on the node or
    /// moves on the slider is one number. A tiling repeats in x and y, a colour is a grey in all four, and
    /// everything else lives in x.
    /// </summary>
    /// <summary>
    /// How many numbers a constant is edited as: a tiling is two (X and Y, each its own box), a colour four,
    /// a scalar one, an engine-fed constant none.
    /// </summary>
    public static int ComponentsOf(string p_Name) =>
        (Find(p_Name)?.Kind ?? ConstantKind.Scalar) switch
        {
            ConstantKind.Tiling => 2,
            ConstantKind.Color => 4,
            ConstantKind.Engine => 0,
            _ => 1,
        };

    public static string Expand(string p_Name, string p_Number) =>
        // ⛔ A VECTOR IS LEFT ALONE. The X/Y boxes, the simple form and the bake all store "x,y,z,w" (invariant
        // culture, '.' decimals), and expanding that again gave "3,3,0,0,3,3,0,0,0,0" — inert only because
        // every reader takes the first four components (measured on the AH-1Z cockpit stand-in, 2026-09-22).
        p_Number.Contains(',')
            ? p_Number
            : (Find(p_Name)?.Kind ?? ConstantKind.Scalar) switch
            {
                ConstantKind.Tiling => $"{p_Number},{p_Number},0,0",
                ConstantKind.Color => $"{p_Number},{p_Number},{p_Number},{p_Number}",
                _ => $"{p_Number},0,0,0",
            };

    /// <summary>
    /// The note a constant's node carries: what it is, what one number means on it, and the range the game
    /// itself uses — so a value typed in Preview value is typed knowing what it will do.
    /// </summary>
    public static string Note(WeaponConstant p_Constant, bool p_Vehicle = false)
    {
        var s_Typed = p_Constant.Kind switch
        {
            ConstantKind.Tiling => "Preview value: X and Y, one number each (the same in both keeps the pattern square).",
            ConstantKind.Color => "Preview value: one number for a grey, or r,g,b,a.",
            ConstantKind.Engine => "",
            _ => "Preview value: one number.",
        };

        // The range is a measurement OF A CONTEXT: on a vehicle the vehicles' one, where they differ.
        var s_Range = (p_Vehicle ? p_Constant.VehicleRange : null) ?? p_Constant.Range;

        return $"{p_Constant.Title} — {p_Constant.What} {(s_Typed.Length > 0 ? s_Typed + " " : "")}" +
               $"({char.ToUpperInvariant(s_Range[0])}{s_Range[1..]}.)";
    }
}

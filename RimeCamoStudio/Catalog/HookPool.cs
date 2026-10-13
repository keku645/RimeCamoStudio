using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// One borrowable UI description: a row in the menu can carry OUR name, text and thumbnail only by
/// rewriting a description the client already indexed under this identifier.
/// </summary>
public sealed class Hook
{
    [JsonPropertyName("identifier")] public uint Identifier { get; set; }
    [JsonPropertyName("family")] public string Family { get; set; } = "";
    [JsonPropertyName("asset")] public string Asset { get; set; } = "";
    [JsonPropertyName("vanillaName")] public string VanillaName { get; set; } = "";
    [JsonPropertyName("vanillaCategory")] public string? VanillaCategory { get; set; }
    [JsonPropertyName("vanillaTexture")] public string? VanillaTexture { get; set; }

    /// <summary>
    /// Which of name/description/category/texturePath this description actually accepts. It is per CLASS,
    /// and it is not decoration: one family accepts only `name` and the menu goes on painting the vanilla
    /// one, which is what made the largest family in the census the useless one.
    /// </summary>
    [JsonPropertyName("fields")] public List<string> Fields { get; set; } = new();

    /// <summary>Soldier camos. Off limits, so the tool never spends one.</summary>
    [JsonPropertyName("reserved")] public bool Reserved { get; set; }

    /// <summary>
    /// The weapon that already offers this identifier, if any. Derived by crossing the weapon catalogue,
    /// not stored in the table — and it is the only thing that makes a hook unsafe: while the kit list
    /// holding that weapon is browsed, its own row shows whatever the description says, so the hook cannot
    /// mean one of our camos in that kit.
    /// </summary>
    public string? OwnerWeapon { get; internal set; }

    public bool CarriesThumbnail => Fields.Contains("texturePath");
}

/// <summary>
/// Hands out hooks in a way that SURVIVES the user adding another camo later.
///
/// ⛔ THE ASSIGNMENT CANNOT BE POSITIONAL. The camo a player picked is stored in their loadout BY
/// IDENTIFIER (measured 2026-09-11: two rows of one weapon on one identifier resolve to the first, in the
/// render and in the saved choice), so if adding a camo reshuffled the pool, every loadout out there would
/// silently point at a different camo — and the saga already measured that a broken variation is not a
/// cosmetic bug but a silent crash as the level loads. So the seat is derived from the camo's NAME and
/// probed deterministically, and whatever a project has already been assigned is honoured before anything
/// new is handed out.
///
/// A seat has FOUR SLOTS, one per kit list (see <see cref="Kits"/>): the row's label is written per kit
/// when the accessories screen opens and holds for the whole arrow cycle, so one identifier can mean a
/// different camo in each kit. A camo takes the slots of the kits its weapons belong to; a camo on an
/// all-kit weapon, or on every weapon, takes all four. Within one kit two camos never share a seat.
/// </summary>
public sealed class HookPool
{
    private readonly List<Hook> m_Seats;
    private readonly Kits m_Kits;

    /// <summary>Seat → kit → holder: a camo's name, or "(framework)" for the eight native rows.</summary>
    private readonly Dictionary<uint, Dictionary<string, string>> m_Taken = new();

    private const string c_Framework = "(framework)";

    private HookPool(List<Hook> p_Seats, Kits p_Kits)
    {
        m_Seats = p_Seats;
        m_Kits = p_Kits;
    }

    /// <summary>Seats with every kit slot free — what a camo offered on every weapon still has to choose from.</summary>
    public int Free => m_Seats.Count(p_S => SlotsOf(p_S.Identifier).Count == 0);

    /// <summary>Kit slots still free over the whole table — what camos limited to one kit have to choose from.</summary>
    public int FreeSlots => m_Seats.Sum(p_S => Kits.Names.Count - SlotsOf(p_S.Identifier).Count);

    public int Total => m_Seats.Count;

    /// <summary>The kit table the slots are keyed on.</summary>
    public Kits Kits => m_Kits;

    public static HookPool Load(WeaponCatalog p_Weapons, string? p_Path = null, Kits? p_Kits = null)
    {
        var s_Path = p_Path ?? Path.Combine(AppContext.BaseDirectory, "Data", "hooks.json");
        var s_Hooks = JsonSerializer.Deserialize<List<Hook>>(File.ReadAllText(s_Path))
                      ?? throw new InvalidDataException($"'{s_Path}' is not a hook table.");

        var s_Owner = new Dictionary<uint, string>();
        foreach (var s_Weapon in p_Weapons.Weapons)
        foreach (var s_Existing in s_Weapon.ExistingCamos)
            s_Owner[s_Existing.Identifier] = s_Weapon.Folder;

        foreach (var s_Hook in s_Hooks)
            s_Hook.OwnerWeapon = s_Owner.GetValueOrDefault(s_Hook.Identifier);

        // ⛔ ORDER IS PART OF THE CONTRACT — it decides which seat a name lands on, so it must be the same
        // on every machine and every run. Ownerless families first: a hook nobody offers is safe in every
        // kit, while a weapon camo hook is barred from the kit of the weapon that already shows it. Spending
        // the unconstrained ones first leaves the awkward ones for last instead of wasting them early.
        var s_Seats = s_Hooks
            .Where(p_H => !p_H.Reserved && p_H.CarriesThumbnail)
            .OrderBy(p_H => p_H.OwnerWeapon != null)
            .ThenBy(p_H => p_H.Identifier)
            .ToList();

        var s_Pool = new HookPool(s_Seats, p_Kits ?? Kits.Load());

        // ⛔ THE FRAMEWORK ITSELF SPENDS EIGHT SEATS BEFORE ANY CAMO OF THE STUDIO'S: its native camo rows
        // (the 8 native camos always go on every weapon, no matter what) borrow
        // the eight identifiers listed in Mod.FrameworkMod, in every kit. A studio camo landing on one of
        // them would share its identifier with a base row, and the two would show the same name — one of
        // them a lie.
        s_Pool.Reserve(Mod.FrameworkMod.NativeIdentifiers);
        return s_Pool;
    }

    private Dictionary<string, string> SlotsOf(uint p_Identifier)
    {
        if (!m_Taken.TryGetValue(p_Identifier, out var s_Slots))
            m_Taken[p_Identifier] = s_Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return s_Slots;
    }

    /// <summary>The kit lists a camo on these weapons occupies (see <see cref="Kits.KitsOfWeapons"/>).</summary>
    public IReadOnlyList<string> KitsOf(IReadOnlyCollection<string> p_Weapons) => m_Kits.KitsOfWeapons(p_Weapons);

    /// <summary>Who holds each kit slot of a seat, for whoever prints or checks the assignment.</summary>
    public IReadOnlyDictionary<string, string> HoldersOf(uint p_Identifier) => SlotsOf(p_Identifier);

    /// <summary>
    /// Marks identifiers that are spent OUTSIDE any project — the framework's own rows — in every kit, so no
    /// camo is ever handed one of them. The holder is named so the reason shows up wherever the assignment
    /// is printed.
    /// </summary>
    public void Reserve(IEnumerable<uint> p_Identifiers)
    {
        foreach (var s_Identifier in p_Identifiers)
        {
            var s_Slots = SlotsOf(s_Identifier);
            foreach (var s_Kit in Kits.Names)
                s_Slots.TryAdd(s_Kit, c_Framework);
        }
    }

    /// <summary>The hook behind an identifier a project already holds, or null if the table does not have it.</summary>
    public Hook? Find(uint p_Identifier) => m_Seats.FirstOrDefault(p_S => p_S.Identifier == p_Identifier);

    /// <summary>Hands a seat back, every slot of it, so renaming a camo does not burn the one it used to hold.</summary>
    public void Release(uint p_Identifier) => m_Taken.Remove(p_Identifier);

    /// <summary>
    /// Re-takes the slots a saved project was already given — the kits its weapons belong to — before anything
    /// new is handed out.
    ///
    /// ⛔ A framework reservation is never overwritten: Assign answers a name that already holds a slot with
    /// that seat, so restoring a default camo under its name ("ABU") would hand the game's own ABU row to
    /// anyone who names a camo the same way.
    /// </summary>
    public void Restore(IEnumerable<(string Camo, uint Identifier, IReadOnlyCollection<string> Weapons)> p_Assigned)
    {
        foreach (var (s_Camo, s_Identifier, s_Weapons) in p_Assigned)
        {
            var s_Slots = SlotsOf(s_Identifier);
            foreach (var s_Kit in m_Kits.KitsOfWeapons(s_Weapons))
                if (!s_Slots.TryGetValue(s_Kit, out var s_Holder) || s_Holder != c_Framework)
                    s_Slots[s_Kit] = s_Camo;
        }
    }

    /// <summary>
    /// The hook for a camo. Deterministic in the camo's name, so the same camo lands on the same seat
    /// whatever else the project holds; linear probing from there, which keeps earlier camos where they were
    /// when a new one collides. A seat fits when every kit the camo's weapons belong to is free on it (or
    /// already the camo's own) and none of them is the kit of the weapon that owns the hook.
    /// </summary>
    /// <param name="p_Camo">The camo's name, as the user typed it.</param>
    /// <param name="p_Weapons">Folders of the weapons this camo will be offered on (empty = every weapon).</param>
    public Hook? Assign(string p_Camo, IReadOnlyCollection<string> p_Weapons)
    {
        if (m_Seats.Count == 0)
            return null;

        var s_Needed = m_Kits.KitsOfWeapons(p_Weapons);
        var s_Start = ProbeStart(p_Camo);
        for (var i = 0; i < m_Seats.Count; i++)
        {
            var s_Seat = m_Seats[(s_Start + i) % m_Seats.Count];
            if (!Fits(s_Seat, p_Camo, s_Needed))
                continue;

            // One seat per camo: slots it held elsewhere (its weapons changed) are handed back.
            foreach (var (s_Other, s_Slots) in m_Taken)
                if (s_Other != s_Seat.Identifier)
                    foreach (var s_Kit in s_Slots.Where(p_S => string.Equals(p_S.Value, p_Camo, StringComparison.OrdinalIgnoreCase)).Select(p_S => p_S.Key).ToList())
                        s_Slots.Remove(s_Kit);

            var s_Mine = SlotsOf(s_Seat.Identifier);
            foreach (var s_Kit in s_Needed)
                s_Mine[s_Kit] = p_Camo;

            return s_Seat;
        }

        return null;
    }

    private bool Fits(Hook p_Seat, string p_Camo, IReadOnlyList<string> p_Needed)
    {
        // The owner rule: the kit list showing the hook's own row cannot carry one of our camos on it.
        if (p_Seat.OwnerWeapon != null)
        {
            var s_Blocked = m_Kits.KitsOfWeapons(new[] { p_Seat.OwnerWeapon });
            if (p_Needed.Any(p_K => s_Blocked.Contains(p_K, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        var s_Slots = SlotsOf(p_Seat.Identifier);
        foreach (var s_Kit in p_Needed)
            if (s_Slots.TryGetValue(s_Kit, out var s_Holder) &&
                !string.Equals(s_Holder, p_Camo, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    /// <summary>The seat a name probes from, for a seam that needs two names on one seat.</summary>
    internal int ProbeStart(string p_Camo) => (int) (Fnv1A(p_Camo) % (uint) m_Seats.Count);

    /// <summary>The position of a seat in the probing order, or -1.</summary>
    internal int IndexOf(uint p_Identifier) => m_Seats.FindIndex(p_S => p_S.Identifier == p_Identifier);

    /// <summary>
    /// ⛔ NOT string.GetHashCode(): .NET randomises it per process, so the same camo would land on a
    /// different seat every time the tool is opened — the exact reshuffle this class exists to prevent.
    /// </summary>
    private static uint Fnv1A(string p_Text)
    {
        var s_Hash = 2166136261u;
        foreach (var s_Char in p_Text.Trim().ToLowerInvariant())
            s_Hash = (s_Hash ^ s_Char) * 16777619u;

        return s_Hash;
    }
}

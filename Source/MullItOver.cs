using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OMWMulletOver
{
    public enum MulletOverIntensity
    {
        Off,
        Minor,
        Major,
        Extreme
    }

    public class OMW_MulletOverDef : Def
    {
        public MulletOverIntensity intensity = MulletOverIntensity.Off;
    }

    public class OMW_MulletOverHairColorDef : Def
    {
    }

    public class MullItOverSettings : ModSettings
    {
        public Dictionary<string, float> breakChances = new Dictionary<string, float>();
        public Dictionary<string, MulletOverIntensity> hairIntensities = new Dictionary<string, MulletOverIntensity>();
        public Dictionary<string, bool> hairColorDisabled = new Dictionary<string, bool>();
        public bool debugLogging;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref breakChances, "breakChances", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref hairIntensities, "hairIntensities", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref hairColorDisabled, "hairColorDisabled", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                breakChances ??= new Dictionary<string, float>();
                hairIntensities ??= new Dictionary<string, MulletOverIntensity>();
                hairColorDisabled ??= new Dictionary<string, bool>();
            }
        }

        public float ChanceFor(MentalBreakDef def)
        {
            if (breakChances.TryGetValue(def.defName, out float chance)) return Mathf.Clamp(chance, 0f, 100f);
            return def.intensity switch
            {
                MentalBreakIntensity.Minor => 30f,
                MentalBreakIntensity.Major => 15f,
                MentalBreakIntensity.Extreme => 5f,
                _ => 0f
            };
        }

        public MulletOverIntensity IntensityFor(HairDef hair)
        {
            if (hairIntensities.TryGetValue(hair.defName, out MulletOverIntensity value)) return value;
            OMW_MulletOverDef def = DefDatabase<OMW_MulletOverDef>.GetNamedSilentFail(hair.defName);
            return def?.intensity ?? MulletOverIntensity.Off;
        }

        public void ResetHairSettingsToDefaults()
        {
            hairIntensities.Clear();
            foreach (HairDef hair in DefDatabase<HairDef>.AllDefsListForReading)
            {
                OMW_MulletOverDef def = DefDatabase<OMW_MulletOverDef>.GetNamedSilentFail(hair.defName);
                if (def != null) hairIntensities[hair.defName] = def.intensity;
            }
        }

        public bool HairColorDisabled(ColorDef color)
        {
            if (hairColorDisabled.TryGetValue(color.defName, out bool disabled)) return disabled;
            return DefDatabase<OMW_MulletOverHairColorDef>.GetNamedSilentFail(color.defName) != null;
        }

        public void SetHairColorDisabled(ColorDef color, bool disabled) => hairColorDisabled[color.defName] = disabled;

        public void ResetHairColorsToDefaults() => hairColorDisabled.Clear();

        public List<ColorDef> HairColorsAtStylingStations()
        {
            List<ColorDef> available = new List<ColorDef>();
            foreach (ColorDef color in DefDatabase<ColorDef>.AllDefsListForReading)
            {
                if (!color.displayInStylingStationUI || available.Any(existing => existing.color.WithinDiffThresholdFrom(color.color, 0.15f)))
                    continue;
                available.Add(color);
            }
            return available;
        }

        public List<ColorDef> HairColorsAvailableAtStylingStations() =>
            HairColorsAtStylingStations().Where(color => !HairColorDisabled(color)).ToList();
    }

    internal sealed class MulletOverTraitSnapshot
    {
        public TraitDef def;
        public int degree;
    }

    internal sealed class MulletOverBaselineProfile
    {
        public List<MentalBreakDef> breakDefs = new List<MentalBreakDef>();
        public List<MulletOverTraitSnapshot> traits = new List<MulletOverTraitSnapshot>();
        public Ideo ideo;
        public XenotypeDef xenotype;
    }

    internal static class MulletOverBaselineBreaks
    {
        private static readonly Dictionary<int, MulletOverBaselineProfile> cache = new Dictionary<int, MulletOverBaselineProfile>();

        public static MulletOverBaselineProfile ForMap(Map map)
        {
            if (map == null || Faction.OfPlayerSilentFail == null) return null;
            if (cache.TryGetValue(map.uniqueID, out MulletOverBaselineProfile cached)) return cached;

            Pawn probe = null;
            Rand.PushState(871923);
            try
            {
                PawnGenerationRequest request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    tile: map.Tile, forceGenerateNewPawn: true, developmentalStages: DevelopmentalStage.Adult);
                probe = PawnGenerator.GeneratePawn(request);
                if (!CellFinder.TryFindRandomCell(map, c => c.Walkable(map) && c.GetThingList(map).Count == 0, out IntVec3 cell))
                    return null;
                GenSpawn.Spawn(probe, cell, map, WipeMode.Vanish);

                MulletOverBaselineProfile profile = new MulletOverBaselineProfile
                {
                    ideo = probe.Ideo,
                    xenotype = probe.genes?.Xenotype
                };
                if (probe.story?.traits != null)
                {
                    foreach (Trait trait in probe.story.traits.allTraits)
                    {
                        profile.traits.Add(new MulletOverTraitSnapshot { def = trait.def, degree = trait.Degree });
                    }
                }
                foreach (MentalBreakDef def in DefDatabase<MentalBreakDef>.AllDefsListForReading
                    .OrderBy(d => d.intensity).ThenBy(d => d.defName, StringComparer.OrdinalIgnoreCase))
                {
                    if (def.Worker != null && def.Worker.BreakCanOccur(probe)) profile.breakDefs.Add(def);
                }
                cache[map.uniqueID] = profile;
                return profile;
            }
            catch (Exception ex)
            {
                Log.Error("[OMW] Failed to test mental-break eligibility on a baseline colonist: " + ex);
                return null;
            }
            finally
            {
                if (probe != null)
                {
                    if (probe.Spawned) probe.DeSpawn();
                    if (Find.WorldPawns.Contains(probe)) Find.WorldPawns.RemovePawn(probe);
                    probe.Destroy();
                }
                Rand.PopState();
            }
        }

        public static void ApplyBaselineProfile(Pawn pawn, MulletOverBaselineProfile profile)
        {
            if (pawn == null || profile == null) return;
            if (pawn.story?.traits != null)
            {
                foreach (Trait trait in pawn.story.traits.allTraits.Where(t => t.sourceGene == null).ToList())
                    pawn.story.traits.RemoveTrait(trait, unsuppressConflicts: true);
                foreach (MulletOverTraitSnapshot snapshot in profile.traits)
                {
                    if (snapshot.def != null && !pawn.story.traits.HasTrait(snapshot.def))
                        pawn.story.traits.GainTrait(new Trait(snapshot.def, snapshot.degree));
                }
            }
            if (pawn.ideo != null && profile.ideo != null) pawn.ideo.SetIdeo(profile.ideo);
        }
    }

    public class OMWMulletOverMod : Mod
    {
        public static MullItOverSettings Settings;
        private int selectedTab;
        private Vector2 scroll;
        private Vector2 hairarchyScroll;
        private Vector2 hairColorScroll;

        public OMWMulletOverMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<MullItOverSettings>();
            var harmony = new Harmony("oldmanwhistler.MullItOver");
            harmony.PatchAll();
        }

        internal static void LogConfiguredHairStyles()
        {
            if (Settings?.debugLogging != true) return;
            foreach (MulletOverIntensity intensity in Enum.GetValues(typeof(MulletOverIntensity)))
            {
                List<string> styles = DefDatabase<HairDef>.AllDefsListForReading
                    .Where(h => Settings.IntensityFor(h) == intensity)
                    .Select(h => h.defName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                string listing = styles.Count > 0 ? string.Join(", ", styles) : intensity == MulletOverIntensity.Off ? "(none)" : "(none; Bald fallback)";
                // Don't log the "Off" intensity if there are any other styles configured, since that will be the default for all other hairstyles.
                if ((intensity == MulletOverIntensity.Off) && styles.Count > 0) continue;
                Log.Message($"[OMW] {styles.Count} configured hairstyles for {intensity}: {listing}.");
            }
        }

        public override string SettingsCategory() => "OMW_MulletOver_SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect tabRect = new Rect(inRect.x, inRect.y, 150f, inRect.height);
            if (Widgets.ButtonText(new Rect(tabRect.x, tabRect.y, tabRect.width, 36f), "OMW_MulletOver_MentalBreaks".Translate())) selectedTab = 0;
            if (Widgets.ButtonText(new Rect(tabRect.x, tabRect.y + 42f, tabRect.width, 36f), "OMW_MulletOver_HairStyles".Translate())) selectedTab = 1;
            if (Widgets.ButtonText(new Rect(tabRect.x, tabRect.y + 84f, tabRect.width, 36f), "OMW_MulletOver_Hairarchy".Translate())) selectedTab = 2;
            if (Widgets.ButtonText(new Rect(tabRect.x, tabRect.y + 126f, tabRect.width, 36f), "OMW_MulletOver_DyeHard".Translate())) selectedTab = 3;
            if (Widgets.ButtonText(new Rect(tabRect.x, tabRect.y + 168f, tabRect.width, 36f), "OMW_MulletOver_Debug".Translate())) selectedTab = 4;
            Rect content = new Rect(inRect.x + 165f, inRect.y, inRect.width - 165f, inRect.height);
            if (selectedTab == 0) DrawBreakSettings(content);
            else if (selectedTab == 1) DrawHairSettings(content);
            else if (selectedTab == 2) DrawHairarchy(content);
            else if (selectedTab == 3) DrawDyeHard(content);
            else DrawDebugSettings(content);
        }

        private static string IntensityLabel(MulletOverIntensity intensity) => ("OMW_MulletOver_" + intensity).Translate();

        private void DrawBreakSettings(Rect rect)
        {
            MulletOverBaselineProfile profile = MulletOverBaselineBreaks.ForMap(Find.CurrentMap);
            List<MentalBreakDef> defs = profile?.breakDefs ?? new List<MentalBreakDef>();
            Rect scrollRect = rect;
            scrollRect.yMax -= 42f;
            Rect view = new Rect(0f, 0f, scrollRect.width - 16f, Mathf.Max(scrollRect.height * 4f, defs.Count * 50f + 360f));
            Widgets.BeginScrollView(scrollRect, ref scroll, view);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(view);
            listing.Label("OMW_MulletOver_ChanceDescription".Translate());
            if (Find.CurrentMap == null)
                listing.Label("OMW_MulletOver_BreakFilterNeedsMap".Translate());
            listing.Gap(8f);
            foreach (MentalBreakIntensity intensity in new[] { MentalBreakIntensity.Minor, MentalBreakIntensity.Major, MentalBreakIntensity.Extreme })
            {
                listing.Label(IntensityLabel((MulletOverIntensity)intensity));
                listing.GapLine();
                foreach (MentalBreakDef def in defs.Where(d => d.intensity == intensity))
                {
                    float value = Settings.ChanceFor(def);
                    listing.Label(def.defName + "   " + value.ToString("0") + "%");
                    float updated = listing.Slider(value, 0f, 100f);
                    if (Math.Abs(updated - value) > 0.01f) Settings.breakChances[def.defName] = updated;
                }
                listing.Gap(8f);
            }
            listing.End();
            Widgets.EndScrollView();
            if (Widgets.ButtonText(new Rect(rect.x, rect.yMax - 36f, rect.width, 34f), "OMW_MulletOver_Reset".Translate()))
                Settings.breakChances.Clear();
        }

        private void DrawHairSettings(Rect rect)
        {
            var defs = DefDatabase<HairDef>.AllDefsListForReading.OrderBy(h => h.StyleItemCategory.LabelCap.ToString()).ThenBy(h => h.defName).ToList();
            Rect scrollRect = rect;
            scrollRect.yMax -= 42f;
            Rect view = new Rect(0f, 0f, scrollRect.width - 16f, Mathf.Max(scrollRect.height * 5f, defs.Count * 42f + 200f));
            HairDef hoveredHair = null;
            Widgets.BeginScrollView(scrollRect, ref scroll, view);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(view);
            foreach (var group in defs.GroupBy(h => h.StyleItemCategory).OrderBy(g => g.Key.LabelCap.ToString()))
            {
                listing.Label(group.Key.LabelCap);
                listing.GapLine();
                foreach (var modGroup in group.GroupBy(h => h.modContentPack)
                    .OrderBy(g => g.Key?.Name ?? "Unknown mod", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.Key?.PackageId ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    listing.Label("   " + (modGroup.Key?.Name ?? "Unknown mod"));
                    foreach (HairDef hair in modGroup.OrderBy(h => h.defName, StringComparer.OrdinalIgnoreCase))
                    {
                        Rect row = listing.GetRect(42f);
                        if (Mouse.IsOver(row)) hoveredHair = hair;
                        Widgets.DefIcon(new Rect(row.x, row.y, 38f, 38f), hair);
                        Widgets.Label(new Rect(row.x + 46f, row.y, row.width * 0.55f, 38f), hair.LabelCap);
                        Rect button = new Rect(row.xMax - 112f, row.y + 4f, 108f, 30f);
                        MulletOverIntensity current = Settings.IntensityFor(hair);
                        if (Widgets.ButtonText(button, IntensityLabel(current)))
                        {
                            var options = new List<FloatMenuOption>();
                            foreach (MulletOverIntensity option in Enum.GetValues(typeof(MulletOverIntensity)))
                            {
                                MulletOverIntensity captured = option;
                                options.Add(new FloatMenuOption(IntensityLabel(captured), () => Settings.hairIntensities[hair.defName] = captured));
                            }
                            Find.WindowStack.Add(new FloatMenu(options));
                        }
                    }
                }
                listing.Gap(8f);
            }
            listing.End();
            Widgets.EndScrollView();
            if (hoveredHair != null && Event.current != null)
            {
                Vector2 mouse = Event.current.mousePosition;
                Rect preview = new Rect(Mathf.Clamp(mouse.x + 18f, rect.x, Mathf.Max(rect.x, rect.xMax - 384f)), Mathf.Clamp(mouse.y + 18f, rect.y, rect.yMax - 264f), 256f, 256f);
                Widgets.DrawMenuSection(preview);
                Widgets.DefIcon(preview.ContractedBy(8f), hoveredHair);
            }
            float footerWidth = (rect.width - 8f) / 2f;
            if (Widgets.ButtonText(new Rect(rect.x, rect.yMax - 36f, footerWidth, 34f), "OMW_MulletOver_Reset".Translate()))
                Settings.ResetHairSettingsToDefaults();
            if (Widgets.ButtonText(new Rect(rect.x + footerWidth + 8f, rect.yMax - 36f, footerWidth, 34f), "OMW_MulletOver_ExportXml".Translate()))
                ExportHairStylesToXml();
        }

        private static void ExportHairStylesToXml()
        {
            try
            {
                var xml = new StringBuilder();
                xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                xml.AppendLine("<Defs>");
                foreach (HairDef hair in DefDatabase<HairDef>.AllDefsListForReading
                    .Where(h => Settings.IntensityFor(h) != MulletOverIntensity.Off)
                    .OrderBy(h => h.defName, StringComparer.Ordinal))
                {
                    xml.Append("  <OMWMulletOver.OMW_MulletOverDef");
                    if (hair.modContentPack != null && hair.modContentPack.PackageId != ModContentPack.CoreModPackageId)
                        xml.Append(" MayRequire=\"").Append(System.Security.SecurityElement.Escape(hair.modContentPack.PackageId)).Append("\"");
                    xml.AppendLine(">");
                    xml.AppendLine("    <defName>" + System.Security.SecurityElement.Escape(hair.defName) + "</defName>");
                    xml.AppendLine("    <intensity>" + Settings.IntensityFor(hair) + "</intensity>");
                    xml.AppendLine("  </OMWMulletOver.OMW_MulletOverDef>");
                }
                xml.AppendLine("</Defs>");
                string path = Path.Combine(GenFilePaths.ConfigFolderPath, "OMW_MulletOverDefaults.xml");
                Directory.CreateDirectory(GenFilePaths.ConfigFolderPath);
                File.WriteAllText(path, xml.ToString(), new UTF8Encoding(false));
                Messages.Message("OMW_MulletOver_ExportSuccess".Translate(path), MessageTypeDefOf.PositiveEvent);
            }
            catch (Exception ex)
            {
                Log.Error("[OMW] Failed to export hairstyle defaults: " + ex);
                Messages.Message("OMW_MulletOver_ExportFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput);
            }
        }

        private void DrawHairarchy(Rect rect)
        {
            const float imageSize = 128f;
            const float columnGap = 12f;
            const float rowHeight = 156f;
            const int columns = 4;
            MulletOverIntensity[] tiers = { MulletOverIntensity.Minor, MulletOverIntensity.Major, MulletOverIntensity.Extreme };
            var byTier = tiers.ToDictionary(t => t, t => DefDatabase<HairDef>.AllDefsListForReading
                .Where(h => !h.noGraphic && Settings.IntensityFor(h) == t)
                .OrderBy(h => h.LabelCap.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.defName, StringComparer.OrdinalIgnoreCase).ToList());
            int totalRows = byTier.Values.Sum(list => (list.Count + columns - 1) / columns);
            float viewWidth = Mathf.Max(rect.width - 16f, columns * imageSize + (columns - 1) * columnGap);
            float viewHeight = Mathf.Max(rect.height, 100f + tiers.Length * 44f + totalRows * rowHeight);
            Rect viewRect = new Rect(0f, 0f, viewWidth, viewHeight);
            Widgets.BeginScrollView(rect, ref hairarchyScroll, viewRect);
            float y = 0f;
            foreach (MulletOverIntensity tier in tiers)
            {
                Widgets.Label(new Rect(0f, y, viewWidth, 30f), IntensityLabel(tier));
                y += 34f;
                List<HairDef> styles = byTier[tier];
                for (int i = 0; i < styles.Count; i++)
                {
                    int col = i % columns;
                    int row = i / columns;
                    float x = col * (imageSize + columnGap);
                    float itemY = y + row * rowHeight;
                    Widgets.DefIcon(new Rect(x, itemY, imageSize, imageSize), styles[i]);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(new Rect(x, itemY + imageSize, imageSize, 24f), styles[i].LabelCap);
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                y += ((styles.Count + columns - 1) / columns) * rowHeight + 12f;
            }
            Widgets.EndScrollView();
        }

        private void DrawDyeHard(Rect rect)
        {
            const int columns = 4;
            const float tileWidth = 140f;
            const float tileHeight = 124f;
            const float gap = 12f;
            List<ColorDef> colors = Settings.HairColorsAtStylingStations();
            Rect scrollRect = rect;
            scrollRect.yMax -= 42f;
            float viewWidth = Mathf.Max(scrollRect.width - 16f, columns * tileWidth + (columns - 1) * gap);
            int rows = (colors.Count + columns - 1) / columns;
            Rect viewRect = new Rect(0f, 0f, viewWidth, Mathf.Max(scrollRect.height, rows * (tileHeight + gap) + 8f));
            Widgets.BeginScrollView(scrollRect, ref hairColorScroll, viewRect);
            for (int i = 0; i < colors.Count; i++)
            {
                ColorDef color = colors[i];
                int col = i % columns;
                int row = i / columns;
                Rect card = new Rect(col * (tileWidth + gap), row * (tileHeight + gap), tileWidth, tileHeight);
                Rect swatch = new Rect(card.x + 8f, card.y + 6f, card.width - 16f, 82f);
                bool disabled = Settings.HairColorDisabled(color);
                Widgets.DrawMenuSection(card);
                Widgets.DrawBoxSolid(swatch, color.color);
                Widgets.DrawBox(swatch, 1);
                if (disabled)
                {
                    Widgets.DrawBoxSolid(swatch, new Color(0f, 0f, 0f, 0.45f));
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(swatch, "OMW_MulletOver_Disabled".Translate());
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                if (Widgets.ButtonInvisible(card)) Settings.SetHairColorDisabled(color, !disabled);
                if (Mouse.IsOver(card)) Widgets.DrawHighlight(card);
            }
            Widgets.EndScrollView();
            float footerWidth = (rect.width - 8f) / 2f;
            if (Widgets.ButtonText(new Rect(rect.x, rect.yMax - 36f, footerWidth, 34f), "OMW_MulletOver_Reset".Translate()))
                Settings.ResetHairColorsToDefaults();
            if (Widgets.ButtonText(new Rect(rect.x + footerWidth + 8f, rect.yMax - 36f, footerWidth, 34f), "OMW_MulletOver_ExportToDef".Translate()))
                ExportHairColorDefs();
        }

        private static void ExportHairColorDefs()
        {
            try
            {
                var xml = new StringBuilder();
                xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                xml.AppendLine("<Defs>");
                foreach (ColorDef color in Settings.HairColorsAtStylingStations().Where(Settings.HairColorDisabled)
                    .OrderBy(c => c.defName, StringComparer.Ordinal))
                {
                    xml.Append("  <OMWMulletOver.OMW_MulletOverHairColorDef");
                    if (color.modContentPack != null && color.modContentPack.PackageId != ModContentPack.CoreModPackageId)
                        xml.Append(" MayRequire=\"").Append(System.Security.SecurityElement.Escape(color.modContentPack.PackageId)).Append("\"");
                    xml.AppendLine(">");
                    xml.AppendLine("    <defName>" + System.Security.SecurityElement.Escape(color.defName) + "</defName>");
                    xml.AppendLine("  </OMWMulletOver.OMW_MulletOverHairColorDef>");
                }
                xml.AppendLine("</Defs>");
                string path = Path.Combine(GenFilePaths.ConfigFolderPath, "OMW_MulletOverHairColorDefaults.xml");
                Directory.CreateDirectory(GenFilePaths.ConfigFolderPath);
                File.WriteAllText(path, xml.ToString(), new UTF8Encoding(false));
                Messages.Message("OMW_MulletOver_HairColorsExportSuccess".Translate(path), MessageTypeDefOf.PositiveEvent);
            }
            catch (Exception ex)
            {
                Log.Error("[OMW] Failed to export disabled hair colors: " + ex);
                Messages.Message("OMW_MulletOver_HairColorsExportFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput);
            }
        }

        private void DrawDebugSettings(Rect rect)
        {
            Widgets.CheckboxLabeled(new Rect(rect.x, rect.y, rect.width, 30f), "OMW_MulletOver_DebugLogging".Translate(), ref Settings.debugLogging);
            Widgets.Label(new Rect(rect.x, rect.y + 36f, rect.width, 60f), "OMW_MulletOver_DebugDescription".Translate());
        }
    }

    [StaticConstructorOnStartup]
    public static class MulletOverStartup
    {
        static MulletOverStartup() => OMWMulletOverMod.LogConfiguredHairStyles();
    }

    [DefOf]
    public static class MulletOverDefOf
    {
        public static MentalStateDef Mullet_Over;
        static MulletOverDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(MulletOverDefOf));
    }

    [DefOf]
    public static class MulletOverJobDefOf
    {
        public static JobDef Mullet_Over;

        static MulletOverJobDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(MulletOverJobDefOf));
    }

    public class MentalState_MulletOver : MentalState
    {
        public HairDef selectedHair;

        public override string InspectLine => "OMW_MulletOver_StateInspect".Translate(pawn.Named("PAWN"));

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref selectedHair, "selectedHair");
        }
    }

    public class JobDriver_MulletOver : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil setup = ToilMaker.MakeToil("MulletOverSetupHaircut");
            setup.initAction = () =>
            {
                HairDef hair = (pawn.MentalState as MentalState_MulletOver)?.selectedHair;
                pawn.style.nextHairColor = null;
                Color? color = null;
                Thing dye = GenClosest.ClosestThing_Global_Reachable(pawn.Position, pawn.Map,
                    pawn.Map.listerThings.ThingsOfDef(ThingDefOf.Dye), PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn), 9999f, t => !t.IsForbidden(pawn) && pawn.CanReserve(t, 1, 1));
                if (dye != null)
                {
                    List<ColorDef> availableColors = OMWMulletOverMod.Settings.HairColorsAvailableAtStylingStations();
                    if (availableColors.TryRandomElement(out ColorDef selectedColor)) color = selectedColor.color;
                }
                pawn.style.SetupNextLookChangeData(hair, pawn.style.beardDef, pawn.style.FaceTattoo, pawn.style.BodyTattoo, color);
            };
            setup.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return setup;
            yield return Toils_StyleChange.DoLookChange(TargetIndex.A, pawn);
            yield return Toils_StyleChange.FinalizeLookChange();
            Toil recover = ToilMaker.MakeToil("MulletOverRecover");
            recover.initAction = () =>
            {
                if (pawn.MentalState is MentalState_MulletOver state) state.RecoverFromState();
            };
            recover.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return recover;
        }
    }

    public static class MulletOverLogic
    {
        public static bool TryReplaceBreak(Pawn pawn, MentalBreakDef breakDef)
        {
            if (pawn?.Map == null || breakDef == null || !ModsConfig.IdeologyActive || OMWMulletOverMod.Settings == null) return false;
            float chance = OMWMulletOverMod.Settings.ChanceFor(breakDef);
            List<Building> stations = pawn.Map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.StylingStation);
            if (stations.NullOrEmpty())
            {
                LogCheck(pawn, breakDef, chance, false, "no styling station on this map");
                return false;
            }
            if (!Rand.Chance(chance / 100f))
            {
                LogCheck(pawn, breakDef, chance, false, "chance roll failed");
                return false;
            }
            if (!TryBeginHaircut(pawn, (MulletOverIntensity)breakDef.intensity, stations, out string reason))
            {
                LogCheck(pawn, breakDef, chance, false, reason);
                return false;
            }
            LogCheck(pawn, breakDef, chance, true, "haircut started");
            return true;
        }

        public static bool TryBeginHaircut(Pawn pawn, MulletOverIntensity intensity)
        {
            if (pawn?.Map == null || !ModsConfig.IdeologyActive) return false;
            List<Building> stations = pawn.Map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.StylingStation);
            if (!TryBeginHaircut(pawn, intensity, stations, out string reason))
            {
                Messages.Message("OMW_MulletOver_DebugFailed".Translate(reason), pawn, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            Messages.Message("OMW_MulletOver_DebugStarted".Translate(pawn.Named("PAWN")), pawn, MessageTypeDefOf.PositiveEvent, historical: false);
            return true;
        }

        private static bool TryBeginHaircut(Pawn pawn, MulletOverIntensity intensity, List<Building> stations, out string reason)
        {
            reason = "no styling station available";
            if (stations.NullOrEmpty()) return false;
            Thing station = GenClosest.ClosestThing_Global_Reachable(pawn.Position, pawn.Map, stations,
                PathEndMode.InteractionCell, TraverseParms.For(pawn), 9999f,
                t => !t.IsForbidden(pawn) && pawn.CanReserve(t));
            if (station == null)
            {
                reason = "no reachable, reservable styling station";
                return false;
            }

            List<HairDef> eligible = DefDatabase<HairDef>.AllDefsListForReading
                .Where(h => !h.noGraphic && (int)OMWMulletOverMod.Settings.IntensityFor(h) == (int)intensity)
                .ToList();
            HairDef selectedHair;
            if (eligible.NullOrEmpty())
            {
                // Leave Brittney Allow: bald is the default if hair styles are configured for this mental break.
                selectedHair = HairDefOf.Bald;
            }
            else if (!eligible.TryRandomElement(out selectedHair))
            {
                reason = "no eligible hairstyle could be selected";
                return false;
            }

            Job job = JobMaker.MakeJob(MulletOverJobDefOf.Mullet_Over, station);
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
            {
                reason = "the haircut job could not be started";
                return false;
            }
            if (!pawn.mindState.mentalStateHandler.TryStartMentalState(MulletOverDefOf.Mullet_Over,
                "OMW_MulletOver_Reason".Translate(), forced: false, forceWake: false, causedByMood: true))
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                reason = "Mullet Over mental state could not be started";
                return false;
            }
            if (pawn.MentalState is MentalState_MulletOver mulletState) mulletState.selectedHair = selectedHair;
            LogPickedStyle(intensity, selectedHair);
            reason = "haircut started; selected hairstyle: " + selectedHair.defName;
            return true;
        }

        private static void LogPickedStyle(MulletOverIntensity intensity, HairDef hair)
        {
            if (OMWMulletOverMod.Settings?.debugLogging == true)
                Log.Message($"[OMW] Haircut selected: intensity={intensity}; hairstyle={hair.defName} ({hair.LabelCap}).");
        }

        private static void LogCheck(Pawn pawn, MentalBreakDef breakDef, float chance, bool succeeded, string detail)
        {
            if (OMWMulletOverMod.Settings?.debugLogging == true)
                Log.Message($"[OMW] Mullet Over check for {pawn.LabelShort} / {breakDef.defName}: configured {chance:0.#}% chance; {(succeeded ? "SUCCEEDED" : "FAILED")} ({detail}).");
        }
    }

    [HarmonyPatch(typeof(MentalBreaker), nameof(MentalBreaker.TryDoMentalBreak))]
    public static class Patch_MentalBreaker_TryDoMentalBreak
    {
        public static bool Prefix(Pawn ___pawn, MentalBreakDef breakDef, ref bool __result)
        {
            try
            {
                if (!MulletOverLogic.TryReplaceBreak(___pawn, breakDef)) return true;
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[OMW] Mullet Over could not replace a mental break: " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(DebugToolsPawns), "MentalBreak")]
    public static class Patch_DebugToolsPawns_MentalBreak
    {
        public static void Postfix(ref List<DebugActionNode> __result)
        {
            if (__result == null) return;
            foreach (DebugActionNode node in __result)
            {
                MentalBreakDef def = DefDatabase<MentalBreakDef>.GetNamedSilentFail(node.label);
                if (def == null || node.pawnAction == null) continue;
                Action<Pawn> vanillaAction = node.pawnAction;
                MentalBreakDef selectedDef = def;
                node.pawnAction = pawn =>
                {
                    if (!MulletOverLogic.TryReplaceBreak(pawn, selectedDef)) vanillaAction(pawn);
                    DebugActionsUtility.DustPuffFrom(pawn);
                };
            }
        }
    }

    public sealed class MulletOverScheduledBreak : IExposable
    {
        public Pawn pawn;
        public MentalBreakDef breakDef;
        public int triggerTick;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Defs.Look(ref breakDef, "breakDef");
            Scribe_Values.Look(ref triggerTick, "triggerTick", 0);
        }
    }

    public sealed class MulletOverScheduledBreaks : GameComponent
    {
        private List<MulletOverScheduledBreak> scheduled = new List<MulletOverScheduledBreak>();

        public MulletOverScheduledBreaks(Game game)
        {
        }

        public void Schedule(IList<Pawn> pawns, IList<MentalBreakDef> breakDefs, int triggerTick)
        {
            for (int i = 0; i < pawns.Count && i < breakDefs.Count; i++)
                scheduled.Add(new MulletOverScheduledBreak { pawn = pawns[i], breakDef = breakDefs[i], triggerTick = triggerTick });
        }

        public override void GameComponentTick()
        {
            for (int i = scheduled.Count - 1; i >= 0; i--)
            {
                MulletOverScheduledBreak entry = scheduled[i];
                if (Find.TickManager.TicksGame < entry.triggerTick) continue;
                scheduled.RemoveAt(i);
                if (entry.pawn?.Spawned != true || entry.pawn.Dead || entry.breakDef == null) continue;
                bool started = entry.pawn.mindState.mentalBreaker.TryDoMentalBreak("MentalStateReason_Mood".Translate(), entry.breakDef);
                if (!started) Log.Warning("[OMW] Scheduled debug mental break did not start: " + entry.breakDef.defName + " for " + entry.pawn.LabelShort + ".");
            }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref scheduled, "scheduledBreaks", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) scheduled ??= new List<MulletOverScheduledBreak>();
        }
    }

    public static class MulletOverDebugActions
    {
        [DebugAction("Mull It Over", "MulletOver - Spawn pawns on the edge of breaking down", requiresIdeology = true, actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnBreakdownTest()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Messages.Message("OMW_MulletOver_DebugSetupNoMap".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }
            MulletOverBaselineProfile profile = MulletOverBaselineBreaks.ForMap(map);
            if (profile == null || profile.breakDefs.Count == 0)
            {
                Messages.Message("OMW_MulletOver_DebugSetupNoEligibleBreaks".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }
            IntVec3 clickCell = UI.MouseCell();
            if (!clickCell.InBounds(map) || !TryFindTestStationCells(map, clickCell, profile.breakDefs.Count, out List<IntVec3> stationCells))
            {
                Messages.Message("OMW_MulletOver_DebugSetupNoStations".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }
            if (!TryFindDyeCells(map, clickCell, stationCells, out List<IntVec3> dyeCells))
            {
                Messages.Message("OMW_MulletOver_DebugSetupNoDyeCells".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }
            MulletOverScheduledBreaks scheduler = Current.Game.GetComponent<MulletOverScheduledBreaks>();
            if (scheduler == null)
            {
                Messages.Message("OMW_MulletOver_DebugSetupFailed".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }
            List<Pawn> testPawns = new List<Pawn>();
            try
            {
                foreach (MentalBreakDef def in profile.breakDefs)
                {
                    PawnGenerationRequest request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                        tile: map.Tile, forceGenerateNewPawn: true, developmentalStages: DevelopmentalStage.Adult,
                        fixedIdeo: profile.ideo, forcedXenotype: profile.xenotype);
                    Pawn pawn = PawnGenerator.GeneratePawn(request);
                    testPawns.Add(pawn);
                    MulletOverBaselineBreaks.ApplyBaselineProfile(pawn, profile);
                    string displayName = def.LabelCap.ToString();
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = def.defName;
                    pawn.Name = new NameTriple(displayName, displayName, displayName);
                }
            }
            catch (Exception ex)
            {
                foreach (Pawn pawn in testPawns) DestroyUnspawnedPawn(pawn);
                Log.Error("[OMW] Failed to generate breakdown test pawns: " + ex);
                Messages.Message("OMW_MulletOver_DebugSetupFailed".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }

            for (int i = 0; i < profile.breakDefs.Count; i++)
            {
                MentalBreakDef def = profile.breakDefs[i];
                IntVec3 stationCell = stationCells[i];
                Building station = (Building)ThingMaker.MakeThing(ThingDefOf.StylingStation, GenStuff.DefaultStuffFor(ThingDefOf.StylingStation));
                station.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(station, stationCell, map, Rot4.North, WipeMode.Vanish);
                IntVec3 interactionCell = stationCell + ThingDefOf.StylingStation.interactionCellOffset.RotatedBy(Rot4.North);
                Pawn pawn = testPawns[i];
                GenSpawn.Spawn(pawn, interactionCell, map, WipeMode.Vanish);
                if (pawn.needs?.mood != null && def.intensity != MentalBreakIntensity.None)
                    pawn.needs.mood.CurLevel = Mathf.Max(0f, pawn.mindState.mentalBreaker.MentalBreakThresholdFor(def.intensity) - 0.001f);
                if (station == null || !def.Worker.BreakCanOccur(pawn))
                    Log.Warning("[OMW] Test pawn could not pass BreakCanOccur after spawn for " + def.defName + ".");
            }
            SpawnFullDyeStacks(map, dyeCells);
            scheduler.Schedule(testPawns, profile.breakDefs, Find.TickManager.TicksGame + 300);
            Messages.Message("OMW_MulletOver_DebugSetupSuccess".Translate(testPawns.Count), MessageTypeDefOf.PositiveEvent);
        }

        private static bool TryFindTestStationCells(Map map, IntVec3 center, int count, out List<IntVec3> result)
        {
            result = new List<IntVec3>();
            List<CellRect> occupiedRects = new List<CellRect>();
            HashSet<IntVec3> interactions = new HashSet<IntVec3>();
            ThingDef stationDef = ThingDefOf.StylingStation;
            float radius = Mathf.Min(Mathf.Sqrt(map.Size.x * map.Size.x + map.Size.z * map.Size.z), GenRadial.MaxRadialPatternRadius - 0.01f);
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, useCenter: true))
            {
                if (!cell.InBounds(map) || !GenSpawn.CanSpawnAt(stationDef, cell, map, Rot4.North, canWipeEdifices: false)) continue;
                IntVec3 interaction = cell + stationDef.interactionCellOffset.RotatedBy(Rot4.North);
                if (!interaction.InBounds(map) || !interaction.Walkable(map) || interaction.GetThingList(map).Count != 0 || interactions.Contains(interaction)) continue;
                CellRect occupied = GenAdj.OccupiedRect(cell, Rot4.North, stationDef.size);
                if (occupiedRects.Any(existing => existing.Any(occupied.Contains) || existing.Contains(interaction)) || occupied.Any(interactions.Contains)) continue;
                result.Add(cell);
                occupiedRects.Add(occupied);
                interactions.Add(interaction);
                if (result.Count == count) return true;
            }
            result.Clear();
            return false;
        }

        private static bool TryFindDyeCells(Map map, IntVec3 center, List<IntVec3> stationCells, out List<IntVec3> dyeCells)
        {
            dyeCells = new List<IntVec3>();
            ThingDef stationDef = ThingDefOf.StylingStation;
            List<CellRect> stationRects = stationCells.Select(c => GenAdj.OccupiedRect(c, Rot4.North, stationDef.size)).ToList();
            HashSet<IntVec3> interactionCells = stationCells.Select(c => c + stationDef.interactionCellOffset.RotatedBy(Rot4.North)).ToHashSet();
            float radius = Mathf.Min(Mathf.Sqrt(map.Size.x * map.Size.x + map.Size.z * map.Size.z), GenRadial.MaxRadialPatternRadius - 0.01f);
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, useCenter: true))
            {
                if (!cell.InBounds(map) || !cell.Walkable(map) || cell.GetThingList(map).Count != 0 || interactionCells.Contains(cell)) continue;
                if (stationRects.Any(rect => rect.Contains(cell))) continue;
                dyeCells.Add(cell);
                if (dyeCells.Count == 3) return true;
            }
            dyeCells.Clear();
            return false;
        }

        private static void SpawnFullDyeStacks(Map map, List<IntVec3> cells)
        {
            foreach (IntVec3 cell in cells)
            {
                Thing dye = ThingMaker.MakeThing(ThingDefOf.Dye);
                dye.stackCount = dye.def.stackLimit;
                GenSpawn.Spawn(dye, cell, map, WipeMode.Vanish);
            }
        }

        private static void DestroyUnspawnedPawn(Pawn pawn)
        {
            if (pawn == null) return;
            if (pawn.Spawned) pawn.DeSpawn();
            if (Find.WorldPawns.Contains(pawn)) Find.WorldPawns.RemovePawn(pawn);
            pawn.Destroy();
        }

        [DebugAction("Mull It Over", "Mullet Over Minor", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MulletOverMinor(Pawn pawn) => MulletOverLogic.TryBeginHaircut(pawn, MulletOverIntensity.Minor);

        [DebugAction("Mull It Over", "Mullet Over Major", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MulletOverMajor(Pawn pawn) => MulletOverLogic.TryBeginHaircut(pawn, MulletOverIntensity.Major);

        [DebugAction("Mull It Over", "Mullet Over Extreme", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MulletOverExtreme(Pawn pawn) => MulletOverLogic.TryBeginHaircut(pawn, MulletOverIntensity.Extreme);
    }
}

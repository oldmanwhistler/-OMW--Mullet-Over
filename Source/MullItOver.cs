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

    public class MullItOverSettings : ModSettings
    {
        public Dictionary<string, float> breakChances = new Dictionary<string, float>();
        public Dictionary<string, MulletOverIntensity> hairIntensities = new Dictionary<string, MulletOverIntensity>();
        public bool debugLogging;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref breakChances, "breakChances", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref hairIntensities, "hairIntensities", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                breakChances ??= new Dictionary<string, float>();
                hairIntensities ??= new Dictionary<string, MulletOverIntensity>();
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
    }

    public class OMWMulletOverMod : Mod
    {
        public static MullItOverSettings Settings;
        private int selectedTab;
        private Vector2 scroll;

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
            if (Widgets.ButtonText(new Rect(tabRect.x, tabRect.y + 84f, tabRect.width, 36f), "OMW_MulletOver_Debug".Translate())) selectedTab = 2;
            Rect content = new Rect(inRect.x + 165f, inRect.y, inRect.width - 165f, inRect.height);
            if (selectedTab == 0) DrawBreakSettings(content);
            else if (selectedTab == 1) DrawHairSettings(content);
            else DrawDebugSettings(content);
        }

        private static string IntensityLabel(MulletOverIntensity intensity) => ("OMW_MulletOver_" + intensity).Translate();

        private void DrawBreakSettings(Rect rect)
        {
            var defs = DefDatabase<MentalBreakDef>.AllDefsListForReading.OrderBy(d => d.intensity).ThenBy(d => d.defName).ToList();
            Rect scrollRect = rect;
            scrollRect.yMax -= 42f;
            Rect view = new Rect(0f, 0f, scrollRect.width - 16f, Mathf.Max(scrollRect.height * 4f, defs.Count * 50f + 360f));
            Widgets.BeginScrollView(scrollRect, ref scroll, view);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(view);
            listing.Label("OMW_MulletOver_ChanceDescription".Translate());
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
                Color color = PawnHairColors.RandomHairColor(pawn, pawn.story.SkinColor, pawn.ageTracker.AgeBiologicalYears);
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

    public static class MulletOverDebugActions
    {
        [DebugAction("Mull It Over", "Mullet Over Minor", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MulletOverMinor(Pawn pawn) => MulletOverLogic.TryBeginHaircut(pawn, MulletOverIntensity.Minor);

        [DebugAction("Mull It Over", "Mullet Over Major", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MulletOverMajor(Pawn pawn) => MulletOverLogic.TryBeginHaircut(pawn, MulletOverIntensity.Major);

        [DebugAction("Mull It Over", "Mullet Over Extreme", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MulletOverExtreme(Pawn pawn) => MulletOverLogic.TryBeginHaircut(pawn, MulletOverIntensity.Extreme);
    }
}

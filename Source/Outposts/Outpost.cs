﻿using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Outposts
{
    [StaticConstructorOnStartup]
    public abstract class Outpost : MapParent
    {
        public static readonly Texture2D PackTex = ContentFinder<Texture2D>.Get("UI/Gizmo/AbandonOutpost");
        public static readonly Texture2D AddTex = ContentFinder<Texture2D>.Get("UI/Gizmo/AddToOutpost");
        public static readonly Texture2D RemoveTex = ContentFinder<Texture2D>.Get("UI/Gizmo/RemovePawnFromOutpost");
        private Material cachedMat;
        public string Name;
        private List<Pawn> occupants = new List<Pawn>();
        private int ticksTillPacked = -1;
        protected int ticksTillProduction;
        public virtual float RestPerTickResting => 0.005714286f * 2.5f;
        public IEnumerable<Pawn> AllPawns => occupants;
        public override Color ExpandingIconColor => Faction.Color;

        public virtual int TicksPerProduction => 15 * 60000;
        public override bool HasName => !Name.NullOrEmpty();
        public override string Label => Name;
        public virtual int TicksToPack => 7 * 60000 / occupants.Count;
        public bool Packing => ticksTillPacked > 0;
        public virtual int Range => -1;

        public override Material Material
        {
            get
            {
                if (cachedMat == null)
                    cachedMat = MaterialPool.MatFrom(Faction.def.settlementTexturePath, ShaderDatabase.WorldOverlayTransparentLit, Faction.Color,
                        WorldMaterials.WorldObjectRenderQueue);

                return cachedMat;
            }
        }

        public override MapGeneratorDef MapGeneratorDef => MapGeneratorDefOf.Base_Faction;

        public virtual ThingDef ProvidedFood => ThingDefOf.MealSimple;

        public int TotalSkil(SkillDef skill)
        {
            return occupants.Sum(p => p.skills.GetSkill(skill).Level);
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (Range > 0) GenDraw.DrawWorldRadiusRing(Tile, Range);
        }

        public override void PostMapGenerate()
        {
            base.PostMapGenerate();
            foreach (var pawn in Map.mapPawns.AllPawns.ListFullCopy()) pawn.Destroy();

            foreach (var occupant in occupants) GenPlace.TryPlaceThing(occupant, Map.Center, Map, ThingPlaceMode.Near);
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            if (!Map.mapPawns.FreeColonists.Any())
            {
                Find.LetterStack.ReceiveLetter("Outposts.Letters.Lost.Label".Translate(), "Outposts.Letters.Lost.Text".Translate(Name), LetterDefOf.NegativeEvent);
                alsoRemoveWorldObject = true;
                return true;
            }

            if (Map.mapPawns.AllPawns.All(p => p.Faction.IsPlayer))
            {
                Find.LetterStack.ReceiveLetter("Outposts.Letters.BattleWon.Label".Translate(), "Outposts.Letters.BattleWon.Text".Translate(Name), LetterDefOf.PositiveEvent,
                    new LookTargets(Gen.YieldSingle(this)));
                alsoRemoveWorldObject = false;
                return true;
            }

            alsoRemoveWorldObject = false;
            return false;
        }

        public static string CheckSkill(IEnumerable<Pawn> pawns, SkillDef skill, int minLevel)
        {
            return pawns.Sum(p => p.skills.GetSkill(skill).Level) < minLevel ? "Outposts.NotSkilledEnough".Translate(skill.skillLabel, minLevel) : null;
        }

        public IEnumerable<Thing> MakeThings(ThingDef thingDef, int count, ThingDef stuff = null)
        {
            count = Mathf.RoundToInt(count * OutpostsMod.Settings.Multiplier);
            while (count > thingDef.stackLimit)
            {
                var temp = ThingMaker.MakeThing(thingDef, stuff);
                temp.stackCount = thingDef.stackLimit;
                yield return temp;
                count -= thingDef.stackLimit;
            }

            var temp2 = ThingMaker.MakeThing(thingDef, stuff);
            temp2.stackCount = count;
            yield return temp2;
        }

        public void Deliver(IEnumerable<Thing> items)
        {
            var map = Find.Maps.Where(m => m.IsPlayerHome).OrderByDescending(m => Find.WorldGrid.ApproxDistanceInTiles(m.Parent.Tile, Tile)).First();
            var dir = Find.WorldGrid.GetRotFromTo(map.Parent.Tile, Tile);
            if (!CellFinder.TryFindRandomEdgeCellWith(x =>
                    !x.Fogged(map) && x.Standable(map) && map.mapPawns.FreeColonistsSpawned.Any(p => p.CanReach(x, PathEndMode.OnCell, Danger.Some)), map,
                dir, CellFinder.EdgeRoadChance_Always, out var cell))
                cell = CellFinder.RandomEdgeCell(dir, map);

            var text = "Outposts.Letters.Items.Text".Translate(Name) + "\n";

            var things = new List<Thing>();

            foreach (var item in items)
            {
                GenPlace.TryPlaceThing(item, cell, map, ThingPlaceMode.Near, (t, i) => things.Add(t));
                text += "  - " + item.LabelCap + "\n";
            }

            Find.LetterStack.ReceiveLetter("Outposts.Letters.Items.Label".Translate(Name), text, LetterDefOf.PositiveEvent, new LookTargets(things));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref occupants, "occupants", LookMode.Deep);
            Scribe_Values.Look(ref ticksTillProduction, "ticksTillProduction");
            RecachePawnTraits();
        }

        public override void Tick()
        {
            base.Tick();
            if (Packing)
            {
                ticksTillPacked--;
                if (ticksTillPacked <= 0) ConvertToCaravan();
            }
            else if (TicksPerProduction > 0)
            {
                ticksTillProduction--;
                if (ticksTillProduction <= 0)
                {
                    ticksTillProduction = TicksPerProduction;
                    Produce();
                }
            }

            if (Find.WorldObjects.PlayerControlledCaravanAt(Tile) is Caravan caravan && !caravan.pather.MovingNow)
                foreach (var pawn in caravan.PawnsListForReading)
                {
                    pawn.needs.rest.CurLevel += RestPerTickResting;
                    if (pawn.IsHashIntervalTick(300))
                    {
                        var food = pawn.needs.food;
                        if (food.CurLevelPercentage <= pawn.RaceProps.FoodLevelPercentageWantEat && ProvidedFood != null && ProvidedFood.IsNutritionGivingIngestible &&
                            ProvidedFood.ingestible.HumanEdible)
                        {
                            var thing = ThingMaker.MakeThing(ProvidedFood);
                            if (thing.IngestibleNow && pawn.RaceProps.CanEverEat(thing)) food.CurLevel += thing.Ingested(pawn, food.NutritionWanted);
                        }
                    }
                }
        }

        public virtual IEnumerable<Thing> ProducedThings()
        {
            yield break;
        }

        public virtual void Produce()
        {
            Deliver(ProducedThings());
        }

        public virtual void RecachePawnTraits()
        {
        }

        public void AddPawn(Pawn pawn)
        {
            var caravan = pawn.GetCaravan();
            if (caravan != null)
            {
                foreach (var item in CaravanInventoryUtility.AllInventoryItems(caravan).Where(item => CaravanInventoryUtility.GetOwnerOf(caravan, item) == item))
                    CaravanInventoryUtility.MoveInventoryToSomeoneElse(pawn, item, caravan.PawnsListForReading, new List<Pawn> {pawn}, item.stackCount);
                caravan.RemovePawn(pawn);
                if (!caravan.PawnsListForReading.Any()) caravan.Destroy();
            }

            Find.WorldPawns.RemovePawn(pawn);
            occupants.Add(pawn);
            RecachePawnTraits();
        }

        public void ConvertToCaravan()
        {
            CaravanMaker.MakeCaravan(occupants, Faction, Tile, true);
            Destroy();
        }

        public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
        {
            return base.GetCaravanGizmos(caravan).Append(new Command_Action
            {
                action = () => Find.WindowStack.Add(new FloatMenu(caravan.PawnsListForReading.Select(p =>
                    new FloatMenuOption(p.NameFullColored.CapitalizeFirst().Resolve(), () => AddPawn(p))).ToList())),
                defaultLabel = "Outposts.Commands.AddPawn.Label".Translate(),
                defaultDesc = "Outposts.Commands.AddPawn.Desc".Translate(),
                icon = AddTex
            });
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            return base.GetGizmos().Concat(new[]
            {
                new Command_Action
                {
                    action = () => ticksTillPacked = TicksToPack,
                    defaultLabel = "Outposts.Commands.Pack.Label".Translate(),
                    defaultDesc = "Outposts.Commands.Pack.Desc".Translate(),
                    icon = PackTex
                },
                new Command_Action
                {
                    action = () => Find.WindowStack.Add(new FloatMenu(occupants.Select(p =>
                        new FloatMenuOption(p.NameFullColored.CapitalizeFirst().Resolve(), () =>
                        {
                            occupants.Remove(p);
                            RecachePawnTraits();
                            CaravanMaker.MakeCaravan(Gen.YieldSingle(p), p.Faction, Tile, true);
                        })).ToList())),
                    defaultLabel = "Outposts.Commands.Remove.Label".Translate(),
                    defaultDesc = "Outposts.Commands.Remove.Desc".Translate(),
                    icon = RemoveTex,
                    disabled = occupants.Count == 1,
                    disabledReason = "Outposts.Command.Remove.Only1".Translate()
                }
            });
        }

        public override string GetInspectString()
        {
            return base.GetInspectString() + "\n" + def.LabelCap + "\n" + "Outposts.Contains".Translate(occupants.Count) +
                   (Packing ? "\n" + "Outposts.Packing".Translate(ticksTillPacked.ToStringTicksToPeriodVerbose()).RawText : "");
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using System.Threading.Tasks;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Plugins.Aspects;

namespace HonedMetalAdditionalIngredients
{
    public class Program
    {
        private static readonly ModKey honedMetalEsp = ModKey.FromNameAndExtension("HonedMetal.esp");

        private static FormLink<IFormListGetter> MakeFormLink(uint id) => new(honedMetalEsp.MakeFormKey(id));

        private static readonly FormLink<IFormListGetter> HM_basic_crafting_materials = MakeFormLink(0x01259E);
        private static readonly FormLink<IFormListGetter> HM_rare_crafting_materials = MakeFormLink(0x019173);

        private static readonly HashSet<IFormLinkGetter<IKeywordGetter>> allowedWorkbenchKeywords = new()
        {
            Skyrim.Keyword.CraftingSmithingArmorTable,
            Skyrim.Keyword.CraftingSmithingForge,
            Skyrim.Keyword.CraftingSmithingSharpeningWheel,
            Skyrim.Keyword.CraftingTanningRack,
            Skyrim.Keyword.CraftingSmithingSkyforge,
            Skyrim.Keyword.CraftingSmelter
        };

        private static readonly HashSet<IFormLinkGetter<IKeywordGetter>> allowedResultWorkbenchKeywords = new()
        {
            Skyrim.Keyword.CraftingSmithingForge,
            Skyrim.Keyword.CraftingTanningRack,
            Skyrim.Keyword.CraftingSmelter
        };

        private static readonly HashSet<IFormLinkGetter<IKeywordGetter>> forbiddenIngredientKeywords = new()
        {
            Skyrim.Keyword.JewelryExpensive,
            Skyrim.Keyword.VendorItemArrow
        };

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "Honed Metal Additional Ingredients.esp")
                .Run(args);
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var basicCraftingMaterialsFormList = HM_basic_crafting_materials.Resolve(state.LinkCache);
            var rareCraftingMaterialsFormList = HM_rare_crafting_materials.Resolve(state.LinkCache);

            IFormLinkGetter<IConstructibleGetter>? toConstructibleFormLink(IFormLinkGetter<ISkyrimMajorRecordGetter> item) => item.TryResolve<IConstructibleGetter>(state.LinkCache)?.AsLinkGetter();

            var basicCraftingMaterialsList = basicCraftingMaterialsFormList.Items.Select(toConstructibleFormLink).Where(i => i is not null).ToHashSet();
            var rareCraftingMaterialsList = rareCraftingMaterialsFormList.Items.Select(toConstructibleFormLink).Where(i => i is not null).ToHashSet();

            var newBasicCraftingMaterialsList = new HashSet<IFormLinkGetter<IConstructibleGetter>>();
            var newRareCraftingMaterialsList = new HashSet<IFormLinkGetter<IConstructibleGetter>>();

            var ingredientFormLinkSet = new HashSet<IFormLinkGetter<IConstructibleGetter>>();

            Dictionary<IFormLinkGetter<IConstructibleGetter>, List<HashSet<IFormLinkGetter<IConstructibleGetter>>>> recipesByResult = new();

            {
                List<IConstructibleObjectGetter> candidateRecipes = new();

                Console.WriteLine("Building a list of candidate recipes, and a list of ingredients in use");

                foreach (var recipe in state.LoadOrder.PriorityOrder.ConstructibleObject().WinningOverrides())
                {
                    if (!allowedWorkbenchKeywords.Contains(recipe.WorkbenchKeyword)) continue;

                    var result = recipe.CreatedObject;
                    if (result.IsNull) continue;
                    if (basicCraftingMaterialsList.Contains(result)) continue;
                    if (rareCraftingMaterialsList.Contains(result)) continue;

                    var ingredients = recipe.Items;
                    if (ingredients is not null)
                        foreach (var ingredient in ingredients)
                        {
                            if (ingredient is null) continue;
                            if (!ingredient.Item.Item.TryResolve<IConstructibleGetter>(state.LinkCache, out var itemRecord)) continue;
                            if (itemRecord is IKeywordedGetter hasKeywords && hasKeywords.Keywords?.Any(keyword => forbiddenIngredientKeywords.Contains(keyword)) == true) continue;
                            var itemLink = itemRecord.AsLinkGetter();
                            if (basicCraftingMaterialsList.Contains(itemLink)) continue;
                            if (rareCraftingMaterialsList.Contains(itemLink)) continue;
                            ingredientFormLinkSet.Add(itemLink);
                        }

                    if (!allowedResultWorkbenchKeywords.Contains(recipe.WorkbenchKeyword)) continue;

                    candidateRecipes.Add(recipe);
                }

                Console.WriteLine($"Found {candidateRecipes.Count} candidate recipes and {ingredientFormLinkSet.Count} candidate ingredients");

                Console.WriteLine("Grouping recipes based on created intermediate (ingredient in at least 1 other recipe) item");

                foreach (var recipe in candidateRecipes)
                {
                    var result = recipe.CreatedObject;
                    if (!ingredientFormLinkSet.Contains(result)) continue;

                    var recipeIngredientSet = new HashSet<IFormLinkGetter<IConstructibleGetter>>();

                    var items = recipe.Items;
                    if (items is not null)
                        foreach (var item in items)
                        {
                            if (item is null) continue;
                            var resolved = toConstructibleFormLink(item.Item.Item);
                            if (resolved is null) continue;
                            recipeIngredientSet.Add(resolved);
                        }

                    if (!recipesByResult.TryGetValue(result, out var list))
                    {
                        list = new();
                        recipesByResult.Add(result, list);
                    }

                    var found = false;

                    foreach (var item in list)
                    {
                        if (item.IsSubsetOf(recipeIngredientSet))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        list.Add(recipeIngredientSet);
                }

            };

            Console.WriteLine($"Found {recipesByResult.Count} potential new basic or rare ingredients (intermediate ingredients)");

            var changed = false;

            do
            {
                changed = false;
                foreach (var (result, ingredientsList) in recipesByResult)
                {
                    if (ingredientsList.Any(ingredients => ingredients.All(ingredient => basicCraftingMaterialsList.Contains(ingredient))))
                    {
                        basicCraftingMaterialsList.Add(result);
                        newBasicCraftingMaterialsList.Add(result);
                        recipesByResult.Remove(result);
                        changed = true;
                        break;
                    }
                    if (ingredientsList.Any(ingredients => ingredients.All(ingredient => basicCraftingMaterialsList.Contains(ingredient) || rareCraftingMaterialsList.Contains(ingredient))))
                    {
                        rareCraftingMaterialsList.Add(result);
                        newRareCraftingMaterialsList.Add(result);
                        recipesByResult.Remove(result);
                        changed = true;
                    }
                }
            } while (changed);

            if (newBasicCraftingMaterialsList.Count > 0)
            {
                Console.WriteLine($"Found {newBasicCraftingMaterialsList.Count} new basic crafting materials");
                state.PatchMod.FormLists.GetOrAddAsOverride(basicCraftingMaterialsFormList).Items.AddRange(newBasicCraftingMaterialsList);
            }

            if (newRareCraftingMaterialsList.Count > 0)
            {
                Console.WriteLine($"Found {newBasicCraftingMaterialsList.Count} new rare crafting materials");
                state.PatchMod.FormLists.GetOrAddAsOverride(rareCraftingMaterialsFormList).Items.AddRange(newRareCraftingMaterialsList);
            }
        }
    }
}

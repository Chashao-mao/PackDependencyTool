using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ModCategoryClassifierTests
{
    [Theory]
    [InlineData("sodium", "Sodium", ModCategory.Performance)]
    [InlineData("fabric-api", "Fabric API", ModCategory.Library)]
    [InlineData("jei", "Just Enough Items", ModCategory.Ui)]
    [InlineData("create", "Create", ModCategory.Tech)]
    [InlineData("journeymap", "JourneyMap", ModCategory.World)]
    [InlineData("iris", "Iris Shaders", ModCategory.Visual)]
    [InlineData("better-combat", "Better Combat", ModCategory.Adventure)]
    [InlineData("mystery-mod", "Mystery Mod", ModCategory.Other)]
    public void ClassifiesCommonMods(string id, string name, ModCategory expected)
    {
        var node = new ModNode { Id = id, Name = name };
        new ModCategoryClassifier().Classify(node);
        Assert.Equal(expected, node.Category);
    }

    [Fact]
    public void ReclassifiesWhenDescriptionChanges()
    {
        var classifier = new ModCategoryClassifier();
        var node = new ModNode { Id = "mystery", Name = "Mystery" };
        classifier.Classify(node);
        Assert.Equal(ModCategory.Other, node.Category);

        node.Description = "A journey map and world generation mod";
        classifier.Classify(node);
        Assert.Equal(ModCategory.World, node.Category);
    }
}

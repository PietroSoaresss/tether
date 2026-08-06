using System.Text.Json;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class CanvasTextStyleTests
{
    [Fact]
    public void TextStyleRoundTrips()
    {
        var original = new Workspace
        {
            Tabs =
            {
                new CanvasTab
                {
                    CanvasItems =
                    {
                        new CanvasItem
                        {
                            Kind = CanvasItemKind.Text,
                            Text = "arquitetura",
                            Size = 24,
                            Font = "serif",
                            Bold = true,
                            Italic = true,
                            Align = "center"
                        }
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(original, TetherJson.Options);
        var loaded = JsonSerializer.Deserialize<Workspace>(json, TetherJson.Options)!;

        var item = loaded.Tabs[0].CanvasItems[0];
        Assert.Equal("serif", item.Font);
        Assert.True(item.Bold);
        Assert.True(item.Italic);
        Assert.Equal("center", item.Align);
        Assert.Equal(24, item.Size);
    }

    /// <summary>
    /// The fields are additive, so a workspace written before they existed has to load with the
    /// defaults rather than nulls — which is why Workspace.Version did not need a bump.
    /// </summary>
    [Fact]
    public void TextWrittenBeforeStylingExistedLoadsWithDefaults()
    {
        const string json = """
            {
              "Version": 1,
              "CanvasItems": [
                { "Kind": "Text", "X": 10, "Y": 20, "Text": "antigo", "Color": "#F5F3F7", "Size": 18 }
              ]
            }
            """;

        var item = Assert.Single(Load(json).Tabs[0].CanvasItems);

        Assert.Equal("antigo", item.Text);
        Assert.Equal("ui", item.Font);
        Assert.Equal("left", item.Align);
        Assert.False(item.Bold);
        Assert.False(item.Italic);
    }

    /// <summary>An explicit null in the file is not the same as an absent field, and crashes downstream.</summary>
    [Fact]
    public void NullStyleFieldsAreRepairedOnLoad()
    {
        const string json = """
            {
              "Version": 1,
              "CanvasItems": [
                { "Kind": "Text", "Text": "nulo", "Font": null, "Align": null, "Color": null }
              ]
            }
            """;

        var item = Assert.Single(Load(json).Tabs[0].CanvasItems);

        Assert.Equal("ui", item.Font);
        Assert.Equal("left", item.Align);
        Assert.Equal("#F5F3F7", item.Color);
    }

    /// <summary>
    /// Items that render as nothing can never be selected or erased, so they only ever accumulate.
    /// A blank text survives being killed mid-edit; a one-point stroke is a click that never became
    /// a drag; a shape needs two corners.
    /// </summary>
    [Fact]
    public void ItemsThatCannotBeSeenAreDroppedOnLoad()
    {
        const string json = """
            {
              "Version": 1,
              "CanvasItems": [
                { "Kind": "Text", "Text": "  \r\n " },
                { "Kind": "Stroke", "Points": [ { "X": 1, "Y": 2 } ] },
                { "Kind": "Rectangle", "Points": [ { "X": 1, "Y": 2 } ] },
                { "Kind": "Text", "Text": "fica" },
                { "Kind": "Stroke", "Points": [ { "X": 1, "Y": 2 }, { "X": 3, "Y": 4 } ] }
              ]
            }
            """;

        var kept = Load(json).Tabs[0].CanvasItems;

        Assert.Equal(2, kept.Count);
        Assert.Equal("fica", kept[0].Text);
        Assert.Equal(CanvasItemKind.Stroke, kept[1].Kind);
    }

    private static Workspace Load(string json)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var paths = new TetherPaths(root);
            File.WriteAllText(paths.WorkspaceFile, json);
            return new WorkspaceStore(paths).Load();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

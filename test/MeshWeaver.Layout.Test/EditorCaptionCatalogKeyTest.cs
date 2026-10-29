using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A generated form's CAPTION follows the viewer's language: a <c>[DisplayName]</c> that names a key
/// of the string catalog renders as that key's text for the viewer, and a name that is not a key
/// renders as written (Doc/GUI/Editor → "Captions in the viewer's language").
///
/// <para>Before, <c>Edit&lt;T&gt;()</c> painted the declared name verbatim, so the caption of every
/// generated form was English for every viewer while its description beneath was translated.</para>
/// </summary>
public class EditorCaptionCatalogKeyTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A form with one catalog-keyed caption and one literal caption.</summary>
    public record Captioned
    {
        /// <summary>Captioned by a catalog key.</summary>
        [DisplayName("serviceIdentities.field.role")]
        public string? Role { get; init; }

        /// <summary>Captioned by a literal that is not a catalog key.</summary>
        [DisplayName("Literal caption")]
        public string? Other { get; init; }
    }

    private static UiControl Form(LayoutAreaHost host, RenderingContext _) => host.Edit(new Captioned(), "captioned");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddLayout(layout => layout.WithView(nameof(Form), Form));

    private async Task<string?[]> Captions(string locale)
    {
        var host = GetHost();
        var access = host.ServiceProvider.GetRequiredService<AccessService>();
        ISynchronizationStream<EntityStore> stream;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "viewer", Name = "Viewer", Locale = locale }))
            stream = host.GetWorkspace().GetStream(new LayoutAreaReference(nameof(Form)))!;
        var control = await stream.GetControlStream(nameof(Form))
            .Should().Within(10.Seconds()).Match(x => x is EditorControl);
        return control.Should().BeOfType<EditorControl>().Subject.Areas
            .Select(a => a.Skins.OfType<PropertySkin>().Single().Label as string)
            .ToArray();
    }

    /// <summary>A German viewer reads the catalog's German text; the literal is unchanged.</summary>
    [Fact]
    public async Task ACatalogKeyCaption_IsInTheViewersLanguage()
        => (await Captions("de")).Should().Equal("Rolle", "Literal caption");

    /// <summary>The control: an English viewer reads the catalog's English text, never the raw key.</summary>
    [Fact]
    public async Task ACatalogKeyCaption_ForAnEnglishViewer_IsTheEnglishText()
        => (await Captions("en")).Should().Equal("Role", "Literal caption");
}

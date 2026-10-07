using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GlbExport;
using Xunit;

public class OptionsTests
{
    private static readonly (string, string, int)[] Cats =
    {
        ("Doors", "OST_Doors", 50), ("Walls", "OST_Walls", 200), ("Topography", "OST_Topography", 1), ("Floors", "OST_Floors", 30),
    };

    [Fact]
    public void Quest_aplica_reglas_y_ordena_por_cantidad()
    {
        var rows = Options.MakeRows("Quest (ligero)", Cats);
        Assert.Equal("Walls", rows[0].Name);
        var doors = rows.Single(r => r.Ost == "OST_Doors");
        Assert.Equal(3, doors.Ratio);
        Assert.Equal(10, doors.ErrorCm);
    }

    [Fact]
    public void Sin_optimizar_no_simplifica_nada()
    {
        var rules = Options.BuildRules(Options.MakeRows("Sin optimizar", Cats), new General());
        Assert.Empty((Dictionary<string, object>)rules["byCategory"]);
    }

    [Fact]
    public void Reglas_separan_piezas_y_superficies_abiertas()
    {
        var rules = Options.BuildRules(Options.MakeRows("Quest (ligero)", Cats), new General());
        var keep = (List<string>)rules["keepSeparate"];
        Assert.Contains("Doors", keep); Assert.Contains("Floors", keep); Assert.DoesNotContain("Walls", keep);
        Assert.Contains("Topography", (List<string>)rules["doubleSided"]);
        Assert.Equal(false, rules["doubleSidedAll"]);
    }

    [Fact]
    public void Categoria_excluida_no_genera_regla_y_se_lista()
    {
        var rows = Options.MakeRows("Quest (ligero)", Cats);
        rows.Single(r => r.Ost == "OST_Doors").Include = false;
        var rules = Options.BuildRules(rows, new General());
        Assert.False(((Dictionary<string, object>)rules["byCategory"]).ContainsKey("Doors"));
        Assert.Contains("Doors", Options.ExcludedNames(rows));
    }

    [Fact]
    public void Valores_invalidos_se_acotan()
    {
        Assert.Equal(100, Options.Clamp("500", 1, 100, 50));
        Assert.Equal(50, Options.Clamp("abc", 1, 100, 50));
        Assert.Equal(1.5, Options.Clamp("1,5", 0, 10, 0));
        Assert.Equal(0, Options.MinCell(1));
        Assert.Equal(20, Options.MinCell(20));
    }

    [Fact]
    public void Sin_instancias_sube_el_minimo()
    {
        var rules = Options.BuildRules(Options.MakeRows("Equilibrado", Cats), new General { UseInstances = false });
        Assert.True((int)rules["minInstances"] > 1_000_000);
    }

    [Fact]
    public void Perfil_se_guarda_y_se_lee_y_uno_danado_da_valores_por_defecto()
    {
        var path = Path.Combine(Path.GetTempPath(), "glb-" + System.Guid.NewGuid().ToString("N") + ".profile");
        try
        {
            var rows = Options.MakeRows("Equilibrado", Cats);
            rows.Single(r => r.Ost == "OST_Walls").Ratio = 77.5;
            Options.SaveProfile("Equilibrado", new General { CellM = 20, OneSided = false }, rows, path);
            var p = Options.LoadProfile(path);
            Assert.Equal("Equilibrado", p.Preset);
            Assert.Equal(20, p.General.CellM);
            Assert.False(p.General.OneSided);
            Assert.Equal(77.5, p.Rows["OST_Walls"].Ratio);
            File.WriteAllText(path, "basura\n=\nrow.X=1;2\npreset=Nada");
            Assert.Equal(Options.DefaultPreset, Options.LoadProfile(path).Preset);
            Assert.Equal(Options.DefaultPreset, Options.LoadProfile(path + ".no").Preset);
        }
        finally { File.Delete(path); }
    }
}

public class JsonWriterTests
{
    [Fact]
    public void Serialize_produce_json_valido()
    {
        var rules = Options.BuildRules(Options.MakeRows("Quest (ligero)", new[] { ("Do\"ors\\", "OST_Doors", 5) }), new General());
        using var doc = JsonDocument.Parse(JsonWriter.Serialize(rules));
        Assert.Equal(0.03, doc.RootElement.GetProperty("byCategory").GetProperty("Do\"ors\\").GetProperty("ratio").GetDouble());
        Assert.Equal(300, doc.RootElement.GetProperty("minTris").GetInt32());
    }

    [Fact]
    public void Escritor_en_flujo_separa_con_comas_y_anida()
    {
        var ms = new MemoryStream();
        using (var w = new JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("a", 1);
            w.WriteStartArray("b"); w.WriteNumberValue(1.5); w.WriteNumberValue(2);
            w.WriteStartObject(); w.WriteBoolean("x", true); w.WriteString("y", "é\n"); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartArray("vacio"); w.WriteEndArray();
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        var b = doc.RootElement.GetProperty("b");
        Assert.Equal(3, b.GetArrayLength());
        Assert.Equal("é\n", b[2].GetProperty("y").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("vacio").GetArrayLength());
    }
}

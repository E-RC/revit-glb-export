using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace GlbExport
{
    /// <summary>
    /// Exporta la vista 3D activa a un formato intermedio (scene.json + data.bin) que GlbExport/ convierte a GLB.
    /// CustomExporter entrega la vista tal como se ve (ocultos, filtros, caja de seccion). Las instancias repetidas
    /// se guardan UNA vez como definicion mas una lista de transformaciones; la geometria directa se junta por
    /// categoria y material. Todo queda en pies y ejes de Revit; el paso a metros / Y arriba lo hace el conversor Node.
    /// Dos pasadas: (1) firma de cada instancia para agruparlas; (2) extraccion real, leyendo la geometria solo de
    /// la primera instancia de cada grupo (el resto se salta con RenderNodeAction.Skip).
    /// </summary>
    public static class SceneExporter
    {
        private const int Schema = 1;
        private static readonly double[] Ident = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
        private const string NoCategory = "(sin categoria)";

        public sealed class Stats
        {
            public int Instances, Defs, Materials;
            public long TrisDefs, TrisStatic, TrisRendered, Bytes;
            public double Seconds;
        }

        // ---------- utilidades de transformacion (3x4 por filas) ----------

        private static double[] Tf(Transform t)
        {
            XYZ x = t.BasisX, y = t.BasisY, z = t.BasisZ, o = t.Origin;
            return new[] { x.X, y.X, z.X, o.X, x.Y, y.Y, z.Y, o.Y, x.Z, y.Z, z.Z, o.Z };
        }

        /// <summary>a * b (aplica b y luego a).</summary>
        private static double[] Mul(double[] a, double[] b) => new[]
        {
            a[0] * b[0] + a[1] * b[4] + a[2] * b[8],
            a[0] * b[1] + a[1] * b[5] + a[2] * b[9],
            a[0] * b[2] + a[1] * b[6] + a[2] * b[10],
            a[0] * b[3] + a[1] * b[7] + a[2] * b[11] + a[3],
            a[4] * b[0] + a[5] * b[4] + a[6] * b[8],
            a[4] * b[1] + a[5] * b[5] + a[6] * b[9],
            a[4] * b[2] + a[5] * b[6] + a[6] * b[10],
            a[4] * b[3] + a[5] * b[7] + a[6] * b[11] + a[7],
            a[8] * b[0] + a[9] * b[4] + a[10] * b[8],
            a[8] * b[1] + a[9] * b[5] + a[10] * b[9],
            a[8] * b[2] + a[9] * b[6] + a[10] * b[10],
            a[8] * b[3] + a[9] * b[7] + a[10] * b[11] + a[11],
        };

        private static double Det(double[] m) =>
            m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);

        /// <summary>Id numerico de un ElementId (long desde Revit 2024, int antes).</summary>
        public static long Eid(ElementId id)
        {
#if REVIT2023
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        // ---------- contexto base ----------

        private abstract class BaseContext : IExportContext
        {
            protected readonly List<Document> Docs = new List<Document>();
            protected readonly List<double[]> Links = new List<double[]> { Ident };
            protected readonly HashSet<string> Exclude;
            protected readonly bool IncludeLinks;
            protected int Muted;                 // >0 dentro de un vinculo que no se incluye
            protected string Cat = NoCategory;
            public bool Cancelled;

            protected BaseContext(Document doc, HashSet<string> exclude, bool includeLinks)
            {
                Docs.Add(doc);
                Exclude = exclude ?? new HashSet<string>();
                IncludeLinks = includeLinks;
            }

            public virtual bool Start() => true;
            public virtual void Finish() { }
            public bool IsCanceled() => Cancelled;
            public virtual RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;
            public virtual void OnViewEnd(ElementId elementId) { }

            public RenderNodeAction OnElementBegin(ElementId elementId)
            {
                if (Muted > 0) return RenderNodeAction.Skip;
                try
                {
                    var el = Docs[Docs.Count - 1].GetElement(elementId);
                    Cat = el?.Category?.Name ?? NoCategory;
                }
                catch { Cat = NoCategory; }
                return Exclude.Contains(Cat) ? RenderNodeAction.Skip : RenderNodeAction.Proceed;
            }

            public virtual void OnElementEnd(ElementId elementId) { }

            public RenderNodeAction OnLinkBegin(LinkNode node)
            {
                Docs.Add(node.GetDocument());
                Links.Add(Mul(Links[Links.Count - 1], Tf(node.GetTransform())));
                if (!IncludeLinks) Muted++;
                return RenderNodeAction.Proceed;
            }

            public void OnLinkEnd(LinkNode node)
            {
                Docs.RemoveAt(Docs.Count - 1);
                Links.RemoveAt(Links.Count - 1);
                if (!IncludeLinks) Muted--;
            }

            public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
            public void OnFaceEnd(FaceNode node) { }
            public void OnRPC(RPCNode node) { }
            public void OnLight(LightNode node) { }
            public virtual void OnMaterial(MaterialNode node) { }
            public virtual RenderNodeAction OnInstanceBegin(InstanceNode node) => RenderNodeAction.Proceed;
            public virtual void OnInstanceEnd(InstanceNode node) { }
            public virtual void OnPolymesh(PolymeshTopology node) { }
        }

        // ---------- pasada 1: firmas ----------

        private sealed class SignaturePass : BaseContext
        {
            private int _depth;
            private bool _recording;
            private ulong _hash;
            public readonly List<ulong> Sigs = new List<ulong>();

            public SignaturePass(Document doc, HashSet<string> exclude, bool links) : base(doc, exclude, links) { }

            private static ulong Mix(ulong h, long v) => (h ^ (ulong)v) * 1099511628211UL;   // FNV-1a por palabra

            public override RenderNodeAction OnInstanceBegin(InstanceNode node)
            {
                if (_depth == 0) { _recording = true; _hash = 14695981039346656037UL; }
                _depth++;
                return RenderNodeAction.Proceed;
            }

            public override void OnInstanceEnd(InstanceNode node)
            {
                _depth--;
                if (_depth == 0) { Sigs.Add(_hash); _recording = false; }
            }

            public override void OnPolymesh(PolymeshTopology node)
            {
                if (!_recording) return;
                double sx = 0, sy = 0, sz = 0;
                foreach (var p in node.GetPoints()) { sx += p.X; sy += p.Y; sz += p.Z; }
                _hash = Mix(_hash, node.NumberOfFacets);
                _hash = Mix(_hash, node.NumberOfPoints);
                _hash = Mix(_hash, (long)Math.Round(sx * 10000));
                _hash = Mix(_hash, (long)Math.Round(sy * 10000));
                _hash = Mix(_hash, (long)Math.Round(sz * 10000));
            }
        }

        // ---------- pasada 2: geometria ----------

        private sealed class Prim
        {
            public readonly int Mat;
            public readonly List<float> Pos = new List<float>(), Nor = new List<float>();
            public readonly List<uint> Idx = new List<uint>();
            public int Nv;
            public Prim(int mat) { Mat = mat; }
        }

        private sealed class Def
        {
            public int Id; public string Cat;
            public readonly Dictionary<int, Prim> Prims = new Dictionary<int, Prim>();
        }

        private sealed class Inst { public int Def; public double[] M; public bool Mirror; }

        private sealed class MatInfo { public string Name; public int R, G, B; public double Alpha; }

        private sealed class ExtractPass : BaseContext
        {
            private readonly int[] _groups;
            public int Ix = -1;                                  // indice de instancia de primer nivel
            private List<double[]> _stack = new List<double[]>();
            private bool _skipPending, _inInst;
            private int _mat;
            public readonly List<MatInfo> Materials = new List<MatInfo>();
            private readonly Dictionary<string, int> _matIx = new Dictionary<string, int>();
            public readonly List<Def> Defs = new List<Def>();
            private readonly Dictionary<int, int> _defOf = new Dictionary<int, int>();
            public readonly List<Inst> Insts = new List<Inst>();
            private Def _rec;
            public readonly SortedDictionary<(string, int), Prim> Statics =
                new SortedDictionary<(string, int), Prim>(Comparer<(string, int)>.Create((a, b) =>
                {
                    var c = string.CompareOrdinal(a.Item1, b.Item1);
                    return c != 0 ? c : a.Item2.CompareTo(b.Item2);
                }));

            public ExtractPass(Document doc, int[] groups, HashSet<string> exclude, bool links) : base(doc, exclude, links)
            { _groups = groups; }

            public override void OnMaterial(MaterialNode node)
            {
                int r = 178, g = 178, b = 178; double tr = 0; string name = "";
                try
                {
                    var c = node.Color; r = c.Red; g = c.Green; b = c.Blue;
                    tr = Math.Round(node.Transparency, 3);
                    var mid = node.MaterialId;
                    if (mid != null && Eid(mid) > 0)
                        name = Docs[Docs.Count - 1].GetElement(mid)?.Name ?? "";
                }
                catch { r = g = b = 178; tr = 0; name = ""; }
                var key = $"{r},{g},{b},{tr.ToString(System.Globalization.CultureInfo.InvariantCulture)},{name}";
                if (!_matIx.TryGetValue(key, out var i))
                {
                    i = _matIx[key] = Materials.Count;
                    Materials.Add(new MatInfo { Name = name, R = r, G = g, B = b, Alpha = 1.0 - tr });
                }
                _mat = i;
            }

            public override RenderNodeAction OnInstanceBegin(InstanceNode node)
            {
                _skipPending = false;                            // un Skip previo sin OnInstanceEnd
                if (!_inInst)
                {
                    Ix++;
                    var g = _groups[Ix];
                    var world = Mul(Links[Links.Count - 1], Tf(node.GetTransform()));
                    if (_defOf.TryGetValue(g, out var existing))
                    {
                        Insts.Add(new Inst { Def = existing, M = world, Mirror = Det(world) < 0 });
                        _skipPending = true;
                        return RenderNodeAction.Skip;
                    }
                    var d = new Def { Id = Defs.Count, Cat = Cat };
                    Defs.Add(d);
                    _defOf[g] = d.Id;
                    Insts.Add(new Inst { Def = d.Id, M = world, Mirror = Det(world) < 0 });
                    _rec = d;
                    _inInst = true;
                    _stack = new List<double[]> { Ident };
                }
                else
                {
                    _stack.Add(Mul(_stack[_stack.Count - 1], Tf(node.GetTransform())));
                }
                return RenderNodeAction.Proceed;
            }

            public override void OnInstanceEnd(InstanceNode node)
            {
                if (_skipPending) { _skipPending = false; return; }
                if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1);
                else { _stack = new List<double[]>(); _rec = null; _inInst = false; }
            }

            public override void OnPolymesh(PolymeshTopology node)
            {
                Prim prim; double[] m;
                if (_rec != null)
                {
                    m = _stack[_stack.Count - 1];
                    if (!_rec.Prims.TryGetValue(_mat, out prim)) prim = _rec.Prims[_mat] = new Prim(_mat);
                }
                else
                {
                    m = Links[Links.Count - 1];
                    var key = (Cat, _mat);
                    if (!Statics.TryGetValue(key, out prim)) prim = Statics[key] = new Prim(_mat);
                }
                Append(prim, node, m);
            }

            private static void Append(Prim prim, PolymeshTopology node, double[] m)
            {
                var pts = node.GetPoints();
                var facets = node.GetFacets();
                var nrm = node.GetNormals();
                int n = pts.Count, nn = nrm.Count;
                var flat = new float[n * 3];
                for (int i = 0; i < n; i++)
                {
                    double x = pts[i].X, y = pts[i].Y, z = pts[i].Z;
                    flat[i * 3] = (float)(m[0] * x + m[1] * y + m[2] * z + m[3]);
                    flat[i * 3 + 1] = (float)(m[4] * x + m[5] * y + m[6] * z + m[7]);
                    flat[i * 3 + 2] = (float)(m[8] * x + m[9] * y + m[10] * z + m[11]);
                }

                void Rot(XYZ v, out float rx, out float ry, out float rz)
                {
                    rx = (float)(m[0] * v.X + m[1] * v.Y + m[2] * v.Z);
                    ry = (float)(m[4] * v.X + m[5] * v.Y + m[6] * v.Z);
                    rz = (float)(m[8] * v.X + m[9] * v.Y + m[10] * v.Z);
                }

                var dist = node.DistributionOfNormals;
                int baseV = prim.Nv;
                if (dist == DistributionOfNormals.OnEachFacet && nn == facets.Count)
                {
                    // una normal por faceta: se duplican vertices por faceta
                    for (int k = 0; k < facets.Count; k++)
                    {
                        Rot(nrm[k], out var nx, out var ny, out var nz);
                        var f = facets[k];
                        foreach (var vi in new[] { f.V1, f.V2, f.V3 })
                        {
                            prim.Pos.Add(flat[vi * 3]); prim.Pos.Add(flat[vi * 3 + 1]); prim.Pos.Add(flat[vi * 3 + 2]);
                            prim.Nor.Add(nx); prim.Nor.Add(ny); prim.Nor.Add(nz);
                        }
                        prim.Idx.Add((uint)prim.Nv); prim.Idx.Add((uint)(prim.Nv + 1)); prim.Idx.Add((uint)(prim.Nv + 2));
                        prim.Nv += 3;
                    }
                    return;
                }

                prim.Pos.AddRange(flat);
                if (dist == DistributionOfNormals.AtEachPoint && nn == n)
                {
                    for (int i = 0; i < n; i++)
                    { Rot(nrm[i], out var nx, out var ny, out var nz); prim.Nor.Add(nx); prim.Nor.Add(ny); prim.Nor.Add(nz); }
                }
                else   // OnePerFace (o inconsistente): la misma normal para todos los puntos
                {
                    float nx = 0, ny = 0, nz = 1;
                    if (nn > 0) Rot(nrm[0], out nx, out ny, out nz);
                    for (int i = 0; i < n; i++) { prim.Nor.Add(nx); prim.Nor.Add(ny); prim.Nor.Add(nz); }
                }
                foreach (var f in facets)
                {
                    prim.Idx.Add((uint)(baseV + f.V1)); prim.Idx.Add((uint)(baseV + f.V2)); prim.Idx.Add((uint)(baseV + f.V3));
                }
                prim.Nv += n;
            }
        }

        // ---------- escritura ----------

        private static long WriteArray(Stream blob, Array data, int elemSize, long offset, out long start)
        {
            start = offset;
            var bytes = new byte[data.Length * elemSize];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            blob.Write(bytes, 0, bytes.Length);
            return offset + bytes.Length;
        }

        private static long DumpPrim(JsonWriter w, Prim p, Stream blob, long offset, string cat)
        {
            w.WriteStartObject();
            if (cat != null) w.WriteString("cat", cat);
            w.WriteNumber("mat", p.Mat);
            w.WriteNumber("nv", p.Nv);
            w.WriteNumber("nt", p.Idx.Count / 3);
            offset = WriteArray(blob, p.Pos.ToArray(), 4, offset, out var pos); w.WriteNumber("pos", pos);
            offset = WriteArray(blob, p.Nor.ToArray(), 4, offset, out var nor); w.WriteNumber("nor", nor);
            offset = WriteArray(blob, p.Idx.ToArray(), 4, offset, out var idx); w.WriteNumber("idx", idx);
            w.WriteEndObject();
            return offset;
        }

        /// <summary>Exporta `view` a outdir/scene.json + outdir/data.bin. Devuelve estadisticas.</summary>
        public static Stats ExportView(Document doc, View3D view, string outDir, Action<string> log,
                                       HashSet<string> exclude, bool includeLinks)
        {
            log = log ?? (_ => { });
            Directory.CreateDirectory(outDir);
            var t0 = DateTime.UtcNow;

            var p1 = new SignaturePass(doc, exclude, includeLinks);
            RunExport(doc, view, p1);
            var sigIds = new Dictionary<ulong, int>();
            var groups = p1.Sigs.Select(s =>
            {
                if (!sigIds.TryGetValue(s, out var id)) sigIds[s] = id = sigIds.Count;
                return id;
            }).ToArray();
            log($"pasada 1: {groups.Length} instancias, {sigIds.Count} grupos, {(DateTime.UtcNow - t0).TotalSeconds:0} s");

            var t1 = DateTime.UtcNow;
            var p2 = new ExtractPass(doc, groups, exclude, includeLinks);
            RunExport(doc, view, p2);
            if (p2.Ix + 1 != groups.Length)
                throw new InvalidOperationException($"Las dos pasadas no coinciden ({p2.Ix + 1} vs {groups.Length} instancias)");
            log($"pasada 2: {(DateTime.UtcNow - t1).TotalSeconds:0} s");

            long offset = 0;
            var defTris = new Dictionary<int, long>();
            long trisStatic = 0;
            using (var blob = File.Create(Path.Combine(outDir, "data.bin")))
            using (var fs = File.Create(Path.Combine(outDir, "scene.json")))
            using (var w = new JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteNumber("schema", Schema);
                w.WriteString("units", "ft");
                w.WriteString("up", "Z");
                w.WriteString("view", view.Name);

                w.WriteStartArray("materials");
                foreach (var m in p2.Materials)
                {
                    w.WriteStartObject();
                    w.WriteString("name", m.Name);
                    w.WriteStartArray("rgb"); w.WriteNumberValue(m.R); w.WriteNumberValue(m.G); w.WriteNumberValue(m.B); w.WriteEndArray();
                    w.WriteNumber("alpha", m.Alpha);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("defs");
                foreach (var d in p2.Defs)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", d.Id);
                    w.WriteString("cat", d.Cat);
                    w.WriteStartArray("prims");
                    long tris = 0;
                    foreach (var p in d.Prims.Values) { offset = DumpPrim(w, p, blob, offset, null); tris += p.Idx.Count / 3; }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    defTris[d.Id] = tris;
                }
                w.WriteEndArray();

                w.WriteStartArray("insts");
                foreach (var i in p2.Insts)
                {
                    w.WriteStartObject();
                    w.WriteNumber("def", i.Def);
                    w.WriteStartArray("m"); foreach (var v in i.M) w.WriteNumberValue(Math.Round(v, 6)); w.WriteEndArray();
                    w.WriteBoolean("mirror", i.Mirror);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("statics");
                foreach (var kv in p2.Statics)
                {
                    offset = DumpPrim(w, kv.Value, blob, offset, kv.Key.Item1);
                    trisStatic += kv.Value.Idx.Count / 3;
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            var uses = p2.Insts.GroupBy(i => i.Def).ToDictionary(g => g.Key, g => (long)g.Count());
            long trisDefs = defTris.Values.Sum();
            long trisAll = trisStatic + defTris.Sum(kv => kv.Value * (uses.TryGetValue(kv.Key, out var u) ? u : 0));
            var stats = new Stats
            {
                Instances = p2.Insts.Count, Defs = p2.Defs.Count, TrisDefs = trisDefs, TrisStatic = trisStatic,
                TrisRendered = trisAll, Materials = p2.Materials.Count, Bytes = offset,
                Seconds = Math.Round((DateTime.UtcNow - t0).TotalSeconds, 1),
            };
            log($"listo: {stats.Instances} instancias, {stats.Defs} definiciones, {stats.TrisRendered} triangulos, {stats.Seconds} s");
            return stats;
        }

        private static void RunExport(Document doc, View3D view, IExportContext ctx)
        {
            var ex = new CustomExporter(doc, ctx) { IncludeGeometricObjects = false, ShouldStopOnError = false };
            ex.Export(view);
        }

        /// <summary>[(nombre, ost, cantidad)] de categorias de modelo con elementos en la vista.</summary>
        public static List<(string Name, string Ost, int Count)> ViewCategories(Document doc, View view)
        {
            var found = new Dictionary<long, (string Name, string Ost, int Count)>();
            foreach (var el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                var c = el.Category;
                if (c == null || c.CategoryType != CategoryType.Model) continue;
                var key = Eid(c.Id);
                if (!found.TryGetValue(key, out var v))
                {
                    var bic = (BuiltInCategory)(int)key;
                    var ost = Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic.ToString() : "id:" + key;
                    v = (c.Name, ost, 0);
                }
                found[key] = (v.Name, v.Ost, v.Count + 1);
            }
            return found.Values.ToList();
        }
    }
}

using OtterGui;
using Penumbra.GameData;
using Penumbra.GameData.Files;
using Penumbra.Meta.Manipulations;
using Penumbra.String.Classes;

namespace Penumbra.UI.AdvancedWindow;

public partial class ModEditWindow
{
    private class MdlTab : IWritable
    {
        private readonly ModEditWindow _edit;

        public  MdlFile        Mdl         { get; private set; }
        private List<string>[] _attributes;

        public List<Utf8GamePath>? GamePaths { get; private set; }
        public int                 GamePathIndex;

        private bool            _dirty;
        public  bool            PendingIo    { get; private set; }
        public  List<Exception> IoExceptions { get; private set; } = [];

        public MdlTab(ModEditWindow edit, byte[] bytes, string path)
        {
            _edit = edit;

            Initialize(new MdlFile(bytes));

            FindGamePaths(path);
        }

        [MemberNotNull(nameof(Mdl), nameof(_attributes))]
        private void Initialize(MdlFile mdl)
        {
            Mdl = mdl;
            _attributes = CreateAttributes(Mdl);
        }

        /// <inheritdoc/>
        public bool Valid
            => Mdl.Valid;

        /// <inheritdoc/>
        public byte[] Write()
            => Mdl.Write();

        public bool Dirty
        {
            get
            {
                var dirty = _dirty;
                _dirty = false;
                return dirty;
            }
        }

        /// <summary> Find the list of game paths that may correspond to this model. </summary>
        /// <param name="path"> Resolved path to a .mdl. </param>
        private void FindGamePaths(string path)
        {
            // If there's no current mod (somehow), there's nothing to resolve the model within.
            var mod = _edit._editor.Mod;
            if (mod == null)
                return;

            if (!Path.IsPathRooted(path) && Utf8GamePath.FromString(path, out var p))
            {
                GamePaths = [p];
                return;
            }

            PendingIo = true;
            var task = Task.Run(() =>
            {
                // TODO: Is it worth trying to order results based on option priorities for cases where more than one match is found?
                // NOTE: We're using case-insensitive comparisons, as option group paths in mods are stored in lower case, but the mod editor uses paths directly from the file system, which may be mixed case.
                return mod.AllSubMods
                    .SelectMany(m => m.Files.Concat(m.FileSwaps))
                    .Where(kv => kv.Value.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
            });

            task.ContinueWith(t =>
            {
                RecordIoExceptions(t.Exception);
                GamePaths   = t.Result;
                PendingIo   = false;
            });
        }

        private EstManipulation[] GetCurrentEstManipulations()
        {
            var mod = _edit._editor.Mod;
            var option = _edit._editor.Option;
            if (mod == null || option == null)
                return [];

            // Filter then prepend the current option to ensure it's chosen first.
            return mod.AllSubMods
                .Where(subMod => subMod != option)
                .Prepend(option)
                .SelectMany(subMod => subMod.Manipulations)
                .Where(manipulation => manipulation.ManipulationType is MetaManipulation.Type.Est)
                .Select(manipulation => manipulation.Est)
                .ToArray();
        }

        /// <summary> Export model to an interchange format. </summary>
        /// <param name="outputPath"> Disk path to save the resulting file to. </param>
        public void Export(string outputPath, Utf8GamePath mdlPath)
        {
            IEnumerable<SklbFile> skeletons;
            try
            {
                var sklbPaths = _edit._models.ResolveSklbsForMdl(mdlPath.ToString(), GetCurrentEstManipulations());
                skeletons = sklbPaths.Select(ReadSklb).ToArray();
            }
            catch (Exception exception)
            {
                RecordIoExceptions(exception);
                return;
            }

            PendingIo = true;
            _edit._models.ExportToGltf(Mdl, skeletons, outputPath)
                .ContinueWith(task =>
                {
                    RecordIoExceptions(task.Exception);
                    PendingIo   = false;
                });
        }
		
		/// <summary> Import a model from an interchange format. </summary>
        /// <param name="inputPath"> Disk path to load model data from. </param>
        public void Import(string inputPath)
        {
            PendingIo = true;
            _edit._models.ImportGltf(inputPath)
                .ContinueWith(task =>
                {
                    RecordIoExceptions(task.Exception);
                    if (task is { IsCompletedSuccessfully: true, Result: not null })
                    {
                        Initialize(task.Result);
                        _dirty = true;
                    }
                    PendingIo = false;
                });
        }

        private void RecordIoExceptions(Exception? exception)
        {
            IoExceptions = exception switch {
                null                  => [],
                AggregateException ae => ae.Flatten().InnerExceptions.ToList(),
                Exception other       => [other],
            };
        }

        /// <summary> Read a .sklb from the active collection or game. </summary>
        /// <param name="sklbPath"> Game path to the .sklb to load. </param>
        private SklbFile ReadSklb(string sklbPath)
        {
            // TODO: if cross-collection lookups are turned off, this conversion can be skipped
            if (!Utf8GamePath.FromString(sklbPath, out var utf8SklbPath, true))
                throw new Exception($"Resolved skeleton path {sklbPath} could not be converted to a game path.");

            var resolvedPath = _edit._activeCollections.Current.ResolvePath(utf8SklbPath);
            // TODO: is it worth trying to use streams for these instead? I'll need to do this for mtrl/tex too, so might be a good idea. that said, the mtrl reader doesn't accept streams, so...
            var bytes = resolvedPath == null ? _edit._gameData.GetFile(sklbPath)?.Data : File.ReadAllBytes(resolvedPath.Value.ToPath());
            return bytes != null
                ? new SklbFile(bytes)
                : throw new Exception(
                    $"Resolved skeleton path {sklbPath} could not be found. If modded, is it enabled in the current collection?");
        }

        /// <summary> Remove the material given by the index. </summary>
        /// <remarks> Meshes using the removed material are redirected to material 0, and those after the index are corrected. </remarks>
        public void RemoveMaterial(int materialIndex)
        {
            for (var meshIndex = 0; meshIndex < Mdl.Meshes.Length; meshIndex++)
            {
                var newIndex = Mdl.Meshes[meshIndex].MaterialIndex;
                if (newIndex == materialIndex)
                    newIndex = 0;
                else if (newIndex > materialIndex)
                    --newIndex;

                Mdl.Meshes[meshIndex].MaterialIndex = newIndex;
            }

            Mdl.Materials = Mdl.Materials.RemoveItems(materialIndex);
        }

        /// <summary> Create a list of attributes per sub mesh. </summary>
        private static List<string>[] CreateAttributes(MdlFile mdl)
            => mdl.SubMeshes.Select(s => Enumerable.Range(0, 32)
                .Where(idx => ((s.AttributeIndexMask >> idx) & 1) == 1)
                .Select(idx => mdl.Attributes[idx])
                .ToList()
            ).ToArray();

        /// <summary> Obtain the attributes associated with a sub mesh by its index. </summary>
        public IReadOnlyList<string> GetSubMeshAttributes(int subMeshIndex)
            => _attributes[subMeshIndex];

        /// <summary> Remove or add attributes from a sub mesh by its index. </summary>
        /// <param name="subMeshIndex"> The index of the sub mesh to update. </param>
        /// <param name="old"> If non-null, remove this attribute. </param>
        /// <param name="new"> If non-null, add this attribute. </param>
        public void UpdateSubMeshAttribute(int subMeshIndex, string? old, string? @new)
        {
            var attributes = _attributes[subMeshIndex];

            if (old != null)
                attributes.Remove(old);

            if (@new != null)
                attributes.Add(@new);

            PersistAttributes();
        }

        /// <summary> Apply changes to attributes to the file in memory. </summary>
        private void PersistAttributes()
        {
            var allAttributes = new List<string>();

            foreach (var (attributes, subMeshIndex) in _attributes.WithIndex())
            {
                var mask = 0u;

                foreach (var attribute in attributes)
                {
                    var attributeIndex = allAttributes.IndexOf(attribute);
                    if (attributeIndex == -1)
                    {
                        allAttributes.Add(attribute);
                        attributeIndex = allAttributes.Count - 1;
                    }

                    mask |= 1u << attributeIndex;
                }

                Mdl.SubMeshes[subMeshIndex].AttributeIndexMask = mask;
            }

            Mdl.Attributes = [.. allAttributes];
        }
    }
}
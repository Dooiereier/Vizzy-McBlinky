namespace Assets.Scripts.VizzyOrganizer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using ModApi.Craft.Program;
    using ModApi.Craft.Program.Instructions;

    /// <summary>
    /// A folder in the tree built from "//Folder/Subfolder" tag comments in a Vizzy program.
    /// </summary>
    public sealed class VizzyFolderNode
    {
        public string Name { get; }
        public string FullPath { get; }
        public VizzyFolderNode Parent { get; }
        public Dictionary<string, VizzyFolderNode> Children { get; } = new Dictionary<string, VizzyFolderNode>();

        // Instruction ids tagged directly into this folder (not its subfolders).
        public HashSet<int> DirectInstructionIds { get; } = new HashSet<int>();

        public VizzyFolderNode(string name, string fullPath, VizzyFolderNode parent)
        {
            Name = name;
            FullPath = fullPath;
            Parent = parent;
        }

        /// <summary>All ids owned by this folder and everything nested beneath it.</summary>
        public IEnumerable<int> GetAllInstructionIds()
        {
            foreach (var id in DirectInstructionIds)
                yield return id;

            foreach (var child in Children.Values)
                foreach (var id in child.GetAllInstructionIds())
                    yield return id;
        }
    }

    /// <summary>
    /// Builds a folder tree from the "//Folder/Subfolder"-tagged comments in a FlightProgram,
    /// plus the set of instructions that aren't tagged into any folder.
    /// </summary>
    public sealed class VizzyFolderIndex
    {
        public VizzyFolderNode Root { get; } = new VizzyFolderNode(string.Empty, string.Empty, null);
        public HashSet<int> UncategorizedInstructionIds { get; } = new HashSet<int>();

        // Walks program.RootInstructions (proven correct: matches the counts a real program
        // reports). getCommentText reads a comment's text - not exposed on the data model
        // itself (CommentInstruction has no public text property), so the caller supplies it;
        // in practice this comes from ProgramSerializer.SerializeFlightProgram's XML, keyed
        // by instruction id, since that's the one place comment text is actually reachable
        // without guessing at private fields or unproven UI internals.
        public static VizzyFolderIndex Build(FlightProgram program, Func<CommentInstruction, string> getCommentText)
        {
            var index = new VizzyFolderIndex();
            index._getCommentText = getCommentText ?? (_ => null);

            if (program?.RootInstructions != null)
                foreach (var chain in FlattenChains(program.RootInstructions))
                    index.WalkChain(chain);

            return index;
        }

        // Exposes the same chain grouping WalkChain uses, as instruction ids, so a caller can
        // keep a chain's own header (e.g. "receive X with data") visible for context whenever
        // any instruction in that chain matches a filter - the header itself is never tagged.
        public static List<List<int>> GetChainIdGroups(FlightProgram program)
        {
            if (program?.RootInstructions == null)
                return new List<List<int>>();

            return FlattenChains(program.RootInstructions)
                .Select(chain => chain.Select(GetId).ToList())
                .ToList();
        }

        // Flattens each root chain plus every nested For/While/If body into its own flat,
        // ordered list - each nested body becomes an independent chain (folder tags don't
        // cross that boundary), matching the tree ProgramInstruction.Next/FirstChild encodes.
        private static IEnumerable<List<ProgramInstruction>> FlattenChains(IEnumerable<ProgramInstruction> roots)
        {
            var chains = new List<List<ProgramInstruction>>();
            foreach (var root in roots)
                CollectChain(root, chains);
            return chains;
        }

        private static void CollectChain(ProgramInstruction first, List<List<ProgramInstruction>> chains)
        {
            var chain = new List<ProgramInstruction>();
            var instruction = first;
            while (instruction != null)
            {
                chain.Add(instruction);
                if (instruction.SupportsChildren && instruction.FirstChild != null)
                    CollectChain(instruction.FirstChild, chains);
                instruction = instruction.Next;
            }
            chains.Add(chain);
        }

        private Func<CommentInstruction, string> _getCommentText;

        // Walks one already-ordered chain. Tag comments open a new "current folder" that
        // applies forward until the next tag comment or the end of this chain - each chain
        // starts fresh at "no folder", so tags never leak from one chain/scope into another.
        private void WalkChain(IEnumerable<ProgramInstruction> chain)
        {
            VizzyFolderNode currentFolder = null;
            foreach (var instruction in chain)
            {
                if (instruction is CommentInstruction comment)
                {
                    var tag = ParseFolderTag(_getCommentText(comment));
                    if (tag != null)
                    {
                        currentFolder = GetOrCreateFolder(tag);
                        AddId(currentFolder, GetId(instruction));
                        continue;
                    }
                }

                AddId(currentFolder, GetId(instruction));
            }
        }

        private void AddId(VizzyFolderNode folder, int id)
        {
            if (folder == null)
                UncategorizedInstructionIds.Add(id);
            else
                folder.DirectInstructionIds.Add(id);
        }

        // ProgramInstruction implements IInstructionId.Id explicitly, so it's only
        // reachable through the interface, not directly off the instruction.
        private static int GetId(ProgramInstruction instruction) => ((IInstructionId)instruction).Id;

        private VizzyFolderNode GetOrCreateFolder(string[] pathSegments)
        {
            var node = Root;
            var pathSoFar = string.Empty;
            foreach (var segment in pathSegments)
            {
                pathSoFar = pathSoFar.Length == 0 ? segment : pathSoFar + "/" + segment;
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new VizzyFolderNode(segment, pathSoFar, node);
                    node.Children[segment] = child;
                }
                node = child;
            }
            return node;
        }

        // Matches comments like "//AscentGuidance" or "//Subfolder/Booster Separation" -
        // everything after "//" is the path, split on "/", each segment allowed to contain
        // spaces (so multi-word folder/subfolder names work).
        private static string[] ParseFolderTag(string commentText)
        {
            if (string.IsNullOrEmpty(commentText))
                return null;

            var trimmed = commentText.TrimStart();
            if (!trimmed.StartsWith("//"))
                return null;

            var afterSlashes = trimmed.Substring(2).Trim();
            if (afterSlashes.Length == 0)
                return null;

            var segments = afterSlashes.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            return segments.Length == 0 ? null : segments;
        }
    }
}

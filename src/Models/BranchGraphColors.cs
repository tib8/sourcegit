using System;
using System.Collections.Generic;

using Avalonia.Media;

namespace SourceGit.Models
{
    public sealed class BranchGraphColors
    {
        // The UI thread updates the cache. Background ref queries can read the published snapshot.
        internal IReadOnlyDictionary<string, string> RefTips => _refTips;
        internal long Version { get; private set; }

        internal BranchGraphColors Copy() => new()
        {
            _colors = new(_colors, StringComparer.Ordinal),
            _refTips = _refTips,
            Version = Version,
        };

        internal void Apply(BranchGraphColors copy)
        {
            if (Version != copy.Version)
            {
                _colors = new(copy._colors, StringComparer.Ordinal);
                Version = copy.Version;
            }
        }

        internal bool TryGetColor(string reference, out Color color)
        {
            return _colors.TryGetValue(reference, out color);
        }

        internal void Remember(string reference, string sha, Color color)
        {
            // Branch and history refreshes run independently. An old history row must not seed a replaced ref.
            if ((_refTips == null || (_refTips.TryGetValue(reference, out var tip) && tip == sha)) &&
                (!_colors.TryGetValue(reference, out var previous) || previous != color))
            {
                _colors[reference] = color;
                Version++;
            }
        }

        internal void Rename(string oldRef, string newRef)
        {
            Version++;
            MoveColor(oldRef, newRef);
            if (_refTips != null && _refTips.ContainsKey(oldRef))
            {
                var tips = new Dictionary<string, string>(_refTips, StringComparer.Ordinal);
                tips.Remove(oldRef, out var tip);
                tips[newRef] = tip;
                _refTips = tips;
            }
        }

        internal void RenameRemote(string oldName, string newName)
        {
            if (_refTips == null)
                return;

            var oldPrefix = $"refs/remotes/{oldName}/";
            var newPrefix = $"refs/remotes/{newName}/";
            var tips = new Dictionary<string, string>(_refTips, StringComparer.Ordinal);
            foreach (var (oldRef, tip) in _refTips)
            {
                if (!oldRef.StartsWith(oldPrefix, StringComparison.Ordinal))
                    continue;

                var newRef = newPrefix + oldRef[oldPrefix.Length..];
                MoveColor(oldRef, newRef);
                tips.Remove(oldRef);
                tips[newRef] = tip;
            }
            _refTips = tips;
            Version++;
        }

        internal void Forget(string reference)
        {
            Version++;
            _colors.Remove(reference);
            if (_refTips?.ContainsKey(reference) == true)
            {
                var tips = new Dictionary<string, string>(_refTips, StringComparer.Ordinal);
                tips.Remove(reference);
                _refTips = tips;
            }
        }

        internal bool UpdateRefs(IReadOnlyDictionary<string, string> previous, Dictionary<string, string> current, Dictionary<string, string> renames)
        {
            if (!ReferenceEquals(previous, _refTips))
                return false;

            if (HaveSameTips(previous, current))
                return true;

            foreach (var rename in renames)
                MoveColor(rename.Value, rename.Key);

            var removed = new List<string>();
            foreach (var reference in _colors.Keys)
            {
                if (!current.ContainsKey(reference))
                    removed.Add(reference);
            }

            foreach (var reference in removed)
                _colors.Remove(reference);

            _refTips = current;
            Version++;
            return true;
        }

        private static bool HaveSameTips(IReadOnlyDictionary<string, string> previous, Dictionary<string, string> current)
        {
            if (previous == null || previous.Count != current.Count)
                return false;

            foreach (var (reference, tip) in current)
            {
                if (!previous.TryGetValue(reference, out var previousTip) || tip != previousTip)
                    return false;
            }

            return true;
        }

        private void MoveColor(string oldRef, string newRef)
        {
            if (_colors.Remove(oldRef, out var color))
                _colors[newRef] = color;
        }

        private Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
        private volatile Dictionary<string, string> _refTips;
    }
}

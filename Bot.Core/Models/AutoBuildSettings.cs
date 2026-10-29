using System;

namespace Bot.Core.Models
{
    public class AutoBuildSettings
    {
        public int MaxSpeedupMinutes { get; set; } = 30; // Maximum timer length to use speedups on (in minutes)
        public bool EnableAutoSpeedup { get; set; } = true;

        /// <summary>
        /// Buildings never to upgrade, matched against the requirement text when the Kitchen upgrade
        /// needs another building first (e.g. "Storehouse"). Skipping a required building stalls the Kitchen.
        /// </summary>
        public List<string> SkipBuildings { get; set; } = new();
        public DateTime? LastBuildTime { get; set; }
    }
} 
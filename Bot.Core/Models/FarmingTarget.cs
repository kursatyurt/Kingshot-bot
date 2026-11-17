using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public enum ResourceType
    {
        Bread,
        Wood,
        Stone,
        Iron
        // Add other resource types if needed
    }

    public class FarmingTarget
    {
        [JsonPropertyName("resourceType")]
        public ResourceType ResourceType { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; } = 1; // Default to level 1

        /// <summary>Share of the unsecured stock this resource gets (4:4:2:1 = bread:wood:stone:iron); each march goes to the one furthest below its share.</summary>
        [JsonPropertyName("weight")]
        public int Weight { get; set; } = 1;
    }
} 
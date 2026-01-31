using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class RobotConfigStore
    {
        private readonly string _path;

        public RobotConfigStore(string? path = null)
        {
            _path = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TwinSync_Gateway",
                "robots.json");
        }

        public List<RobotConfig> Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<RobotConfig>();

                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<List<RobotConfig>>(json) ?? new List<RobotConfig>();
            }
            catch
            {
                // If file is corrupted or schema changes, start clean.
                return new List<RobotConfig>();
            }
        }

        public void Save(IEnumerable<RobotConfig> robots)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(robots, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_path, json);
        }
    }
}

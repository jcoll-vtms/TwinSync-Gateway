// ===============================
// 3) Services: PlcConfigStore
// File: Services/PlcConfigStore.cs
// ===============================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class PlcConfigStore
    {
        private readonly string _path;

        public PlcConfigStore(string? path = null)
        {
            _path = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TwinSync_Gateway",
                "plcs.json");
        }

        public List<PlcConfig> Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<PlcConfig>();

                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<List<PlcConfig>>(json) ?? new List<PlcConfig>();
            }
            catch
            {
                return new List<PlcConfig>();
            }
        }

        public void Save(IEnumerable<PlcConfig> plcs)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(plcs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
    }
}

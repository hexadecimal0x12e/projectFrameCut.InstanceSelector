using Microsoft.UI.Xaml;
using System;
using System.Buffers;
using System.IO;
using System.Text.Json;

namespace projectFrameCut.InstanceSelector
{
    internal sealed class SelectorSettingsStore
    {
        private static readonly object SettingsLock = new();

        public string SettingsPath { get; } = Path.Combine(
            ProjectDirectoryRegistration.GetLocalStatePath(),
            "settings.json");

        public bool SettingsExist
        {
            get
            {
                lock (SettingsLock)
                {
                    return File.Exists(SettingsPath);
                }
            }
        }

        public string? GetDefaultInstance()
        {
            lock (SettingsLock)
            {
                if (!File.Exists(SettingsPath))
                {
                    return null;
                }

                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    using JsonDocument document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("配置根节点必须是对象。");
                    }

                    if (!document.RootElement.TryGetProperty(
                            "defaultInstance",
                            out JsonElement defaultInstance))
                    {
                        return null;
                    }

                    if (defaultInstance.ValueKind is not JsonValueKind.String and
                        not JsonValueKind.Null)
                    {
                        throw new JsonException("defaultInstance 必须是字符串或 null。");
                    }

                    string? value = defaultInstance.GetString();
                    return string.IsNullOrWhiteSpace(value)
                        ? null
                        : value.Trim();
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    throw new InvalidDataException(
                        $"无法读取默认实例配置“{SettingsPath}”：{ex.Message}",
                        ex);
                }
            }
        }

        public void SetDefaultInstance(string packageIdentifier)
        {
            string normalizedIdentifier = packageIdentifier.Trim();
            if (normalizedIdentifier.Length == 0)
            {
                throw new ArgumentException("默认实例标识符不能为空。", nameof(packageIdentifier));
            }

            lock (SettingsLock)
            {
                string directoryPath = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(directoryPath);

                string temporaryPath = Path.Combine(
                    directoryPath,
                    $"settings.{Guid.NewGuid():N}.tmp");
                try
                {
                    ArrayBufferWriter<byte> buffer = new();
                    using (Utf8JsonWriter writer = new(
                        buffer,
                        new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("defaultInstance", normalizedIdentifier);
                        writer.WriteEndObject();
                    }

                    File.WriteAllBytes(temporaryPath, buffer.WrittenMemory.ToArray());
                    File.Move(temporaryPath, SettingsPath, true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
        }

        public void ClearDefaultInstance()
        {
            lock (SettingsLock)
            {
                if (File.Exists(SettingsPath))
                {
                    File.Delete(SettingsPath);
                }
            }
        }

    }
}

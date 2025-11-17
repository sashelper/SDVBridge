using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SDVBridge.Server
{
    internal static class JsonUtil
    {
        private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true
        };

        public static string Serialize(object value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var serializer = new DataContractJsonSerializer(value.GetType(), Settings);
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("JSON payload is required.", nameof(json));
            }

            var serializer = new DataContractJsonSerializer(typeof(T), Settings);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var result = serializer.ReadObject(ms);
                if (result == null)
                {
                    throw new InvalidOperationException("Unable to deserialize JSON payload.");
                }

                return (T)result;
            }
        }
    }
}

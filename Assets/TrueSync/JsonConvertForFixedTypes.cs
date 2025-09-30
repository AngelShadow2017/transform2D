using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Core.TrueSync
{
    public static class InitConverters
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        internal static void Init()
        {

            // 保存原有的DefaultSettings委托
            var originalSettings = JsonConvert.DefaultSettings;
            Lazy<JsonSerializerSettings> settings = new Lazy<JsonSerializerSettings>(() =>
            {
                // 获取原有设置或创建新实例
                var settings = originalSettings?.Invoke() ?? new JsonSerializerSettings();

                // 确保Converters列表可写
                if (settings.Converters.IsReadOnly)
                {
                    settings.Converters = new List<JsonConverter>(settings.Converters);
                }
                HashSet<Type> converters = new HashSet<Type>(settings.Converters.Select(converter => converter.GetType()));
                // 添加自定义转换器（示例为Type1Converter和Type2Converter）
                AddConverterIfMissing<Fix64Converter>(converters,settings.Converters);
                AddConverterIfMissing<TSVector2Converter>(converters,settings.Converters);
                AddConverterIfMissing<TSVector3Converter>(converters,settings.Converters);
                AddConverterIfMissing<TSVector4Converter>(converters,settings.Converters);
                return settings;
            });
            JsonConvert.DefaultSettings =()=> settings.Value;

            // 辅助方法：检查并添加转换器
            void AddConverterIfMissing<T>(HashSet<Type> already,IList<JsonConverter> converters) where T : JsonConverter, new()
            {
                if (!already.Contains(typeof(T)))
                {
                    converters.Add(new T());
                    already.Add(typeof(T));
                }
            }
        }
    }

    public class Fix64Converter : JsonConverter<FP>
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;

        //public override bool CanConvert(Type objectType) => objectType == typeof(FP);
        public Fix64Converter()
        {
        }

        public override FP ReadJson(JsonReader reader, Type objectType, FP existingValue,bool hasVal, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return new FP();

            if (reader.TokenType == JsonToken.Integer)
                return FP.FromRaw((long)reader.Value);

            // 处理非整数类型的值（如字符串或浮点数）
            try
            {
                var value = Convert.ToInt64(reader.Value);
                return FP.FromRaw(value);
            }
            catch (Exception)
            {
                throw new JsonSerializationException($"无法将值 {reader.Value} 转换为Fix64类型");
            }
        }

        public override void WriteJson(JsonWriter writer, FP value, JsonSerializer serializer)
        {
            writer.WriteValue(value.RawValue); // 直接写入原始值，无需转换为字符串
        }
    }

    // TSVector2 泛型转换器
    public class TSVector2Converter : JsonConverter<TSVector2>
    {
        public TSVector2Converter()
        {
        }

        public override TSVector2 ReadJson(JsonReader reader, Type objectType, TSVector2 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var jArray = serializer.Deserialize<JArray>(reader);
            if (jArray == null || jArray.Count < 2)
                return new TSVector2();

            FP x = ParseFP(jArray[0]);
            FP y = ParseFP(jArray[1]);
            return new TSVector2(x, y);
        }

        public override void WriteJson(JsonWriter writer, TSVector2 value, JsonSerializer serializer)
        {
            var jArray = new JArray(value.x.RawValue, value.y.RawValue);
            jArray.WriteTo(writer);
        }

        private FP ParseFP(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                if (long.TryParse((string)token, out long value))
                    return FP.FromRaw(value);
                throw new JsonSerializationException($"无法将字符串 {token} 转换为Fix64类型");
            }
            if (token.Type == JTokenType.Integer)
                return FP.FromRaw((long)token);
            throw new JsonSerializationException($"无效的类型 {token.Type}，必须为整数或可转换为整数的字符串");
        }
    }

    // TSVector3 泛型转换器
    public class TSVector3Converter : JsonConverter<TSVector>
    {
        public TSVector3Converter()
        {
        }
        public override TSVector ReadJson(JsonReader reader, Type objectType, TSVector existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var jArray = serializer.Deserialize<JArray>(reader);
            if (jArray == null || jArray.Count < 3)
                return new TSVector();

            FP x = ParseFP(jArray[0]);
            FP y = ParseFP(jArray[1]);
            FP z = ParseFP(jArray[2]);
            return new TSVector(x, y, z);
        }

        public override void WriteJson(JsonWriter writer, TSVector value, JsonSerializer serializer)
        {
            var jArray = new JArray(value.x.RawValue, value.y.RawValue, value.z.RawValue);
            jArray.WriteTo(writer);
        }

        private FP ParseFP(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                if (long.TryParse((string)token, out long value))
                    return FP.FromRaw(value);
                throw new JsonSerializationException($"无法将字符串 {token} 转换为Fix64类型");
            }
            if (token.Type == JTokenType.Integer)
                return FP.FromRaw((long)token);
            throw new JsonSerializationException($"无效的类型 {token.Type}，必须为整数或可转换为整数的字符串");
        }
    }

    // TSVector4 泛型转换器
    public class TSVector4Converter : JsonConverter<TSVector4>
    {
        public TSVector4Converter()
        {
        }
        public override TSVector4 ReadJson(JsonReader reader, Type objectType, TSVector4 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var jArray = serializer.Deserialize<JArray>(reader);
            if (jArray == null || jArray.Count < 4)
                return new TSVector4();

            FP x = ParseFP(jArray[0]);
            FP y = ParseFP(jArray[1]);
            FP z = ParseFP(jArray[2]);
            FP w = ParseFP(jArray[3]);
            return new TSVector4(x, y, z, w);
        }

        public override void WriteJson(JsonWriter writer, TSVector4 value, JsonSerializer serializer)
        {
            var jArray = new JArray(value.x.RawValue, value.y.RawValue, value.z.RawValue, value.w.RawValue);
            jArray.WriteTo(writer);
        }

        private FP ParseFP(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                if (long.TryParse((string)token, out long value))
                    return FP.FromRaw(value);
                throw new JsonSerializationException($"无法将字符串 {token} 转换为Fix64类型");
            }
            if (token.Type == JTokenType.Integer)
                return FP.FromRaw((long)token);
            throw new JsonSerializationException($"无效的类型 {token.Type}，必须为整数或可转换为整数的字符串");
        }
    }
}

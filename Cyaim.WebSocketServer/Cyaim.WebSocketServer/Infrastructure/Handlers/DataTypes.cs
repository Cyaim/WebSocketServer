using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    /// <summary>
    /// DataTypes
    /// </summary>
    public static class DataTypes
    {
        /// <summary>
        /// System.DateTime
        /// </summary>
        private const string Type_DateTime = "System.DateTime";

        /// <summary>
        /// System.DateTimeOffset
        /// </summary>
        private const string Type_DateTimeOffset = "System.DateTimeOffset";

        /// <summary>
        /// System.Text.Json.JsonNode
        /// </summary>
        private const string Type_JsonNode = "System.Text.Json.JsonNode";

        /// <summary>
        /// System.Text.Json.JsonObject
        /// </summary>
        private const string Type_JsonObject = "System.Text.Json.JsonObject";

        /// <summary>
        /// System.Text.Json.JsonArray
        /// </summary>
        private const string Type_JsonArray = "System.Text.Json.JsonArray";

        /// <summary>
        /// System.Text.Json.JsonValue
        /// </summary>
        private const string Type_JsonValue = "System.Text.Json.JsonValue";


        #region BaseType

        /// <summary>
        /// System.SByte
        /// </summary>
        private const string Type_SByte = "System.SByte";

        /// <summary>
        /// System.Byte
        /// </summary>
        private const string Type_Byte = "System.Byte";

        /// <summary>
        /// System.Int16
        /// </summary>
        private const string Type_Short = "System.Int16";

        /// <summary>
        /// System.UInt16
        /// </summary>
        private const string Type_UShort = "System.UInt16";

        /// <summary>
        /// System.Int32
        /// </summary>
        private const string Type_Int = "System.Int32";

        /// <summary>
        /// System.UInt32
        /// </summary>
        private const string Type_UInt = "System.UInt32";

        /// <summary>
        /// System.Int64
        /// </summary>
        private const string Type_Long = "System.Int64";

        /// <summary>
        /// System.UInt64
        /// </summary>
        private const string Type_ULong = "System.UInt64";

        /// <summary>
        /// System.Single
        /// </summary>
        private const string Type_Float = "System.Single";

        /// <summary>
        /// System.Double
        /// </summary>
        private const string Type_Double = "System.Double";

        /// <summary>
        /// System.Boolean
        /// </summary>
        private const string Type_Bool = "System.Boolean";

        /// <summary>
        /// System.Char
        /// </summary>
        private const string Type_Char = "System.Char";

        /// <summary>
        /// System.Decimal
        /// </summary>
        private const string Type_Decimal = "System.Decimal";

        /// <summary>
        /// System.String
        /// </summary>
        private const string Type_String = "System.String";

        #endregion

        /// <summary>
        /// JsonNode value to target type
        /// </summary>
        /// <param name="type">target type</param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static object ConvertTo(this Type type, JsonNode value)
        {
            try
            {
                return type.FullName switch
                {
                    Type_String => value.GetValue<string>(),
                    Type_SByte => value.GetValue<sbyte>(),
                    Type_Byte => value.GetValue<byte>(),
                    Type_Short => value.GetValue<short>(),
                    Type_UShort => value.GetValue<ushort>(),
                    Type_Int => value.GetValue<int>(),
                    Type_UInt => value.GetValue<uint>(),
                    Type_Long => value.GetValue<long>(),
                    Type_ULong => value.GetValue<ulong>(),
                    Type_Float => value.GetValue<float>(),
                    Type_Double => value.GetValue<double>(),
                    Type_Bool => value.GetValue<bool>(),
                    Type_DateTime => value.GetValue<DateTime>(),
                    Type_DateTimeOffset => value.GetValue<DateTimeOffset>(),
                    Type_Char => value.GetValue<char>(),
                    Type_Decimal => value.GetValue<decimal>(),
                    _ => value.Deserialize(type)
                };
            }
            catch (Exception)
            {

                throw;
            }
        }

        /// <summary>
        /// object to real type
        /// </summary>
        /// <param name="type">Target type</param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static object ConvertTo(this Type type, object value)
        {
            try
            {
                switch (type.FullName)
                {
                    case Type_String:
                        return Convert.ToString(value);
                    case Type_SByte:
                        return Convert.ToSByte(value);
                    case Type_Byte:
                        return Convert.ToByte(value);
                    case Type_Short:
                        return Convert.ToInt16(value);
                    case Type_UShort:
                        return Convert.ToUInt16(value);
                    case Type_Int:
                        return Convert.ToInt32(value);
                    case Type_UInt:
                        return Convert.ToUInt32(value);
                    case Type_Long:
                        return Convert.ToInt64(value);
                    case Type_ULong:
                        return Convert.ToUInt64(value);
                    case Type_Float:
                        return Convert.ToSingle(value);
                    case Type_Double:
                        return Convert.ToDouble(value);
                    case Type_Bool:
                        return Convert.ToBoolean(value);
                    case Type_Char:
                        return Convert.ToChar(value);
                    case Type_Decimal:
                        return Convert.ToChar(value);
                    case Type_DateTime:
                        return Convert.ToDateTime(value);
                    case Type_DateTimeOffset:
                        return DateTimeOffset.Parse(value.ToString());
                    case Type_JsonNode:
                    case Type_JsonObject:
                    case Type_JsonArray:
                    case Type_JsonValue:
                        return JsonNode.Parse(value.ToString());
                    default:
                        return Convert.ChangeType(value, type);
                }
            }
            catch (Exception)
            {

                throw;
            }
        }

        /// <summary>
        /// Check type is C# define type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static bool IsBasicType(this Type type)
        {
            try
            {
                switch (type.FullName)
                {
                    case Type_String:
                    case Type_SByte:
                    case Type_Byte:
                    case Type_Short:
                    case Type_UShort:
                    case Type_Int:
                    case Type_UInt:
                    case Type_Long:
                    case Type_ULong:
                    case Type_Float:
                    case Type_Double:
                    case Type_Bool:
                    //case Type_DateTime:
                    //case Type_DateTimeOffset:
                    case Type_Char:
                    case Type_Decimal:
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception)
            {

                throw;
            }
        }

        /// <summary>
        /// Check type is Task<> or Task
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static bool IsTaskType(Type type)
        {
            // 检查是否是泛型类型，并且其泛型定义是否与 Task 匹配
            if (type.IsGenericType)
            {
                Type genericTypeDefinition = type.GetGenericTypeDefinition();
                return genericTypeDefinition == typeof(Task<>) || genericTypeDefinition == typeof(Task);
            }

            // 对于非泛型类型，直接比较是否是 Task
            return type == typeof(Task);
        }
    }
}
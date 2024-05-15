using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    public static class DataTypes
    {
        #region BaseType
        public const string Type_SByte = "System.SByte";
        public const string Type_Byte = "System.Byte";
        public const string Type_Short = "System.Int16";
        public const string Type_UShort = "System.UInt16";
        public const string Type_Int = "System.Int32";
        public const string Type_UInt = "System.UInt32";
        public const string Type_Long = "System.Int64";
        public const string Type_ULong = "System.UInt64";
        public const string Type_Float = "System.Single";
        public const string Type_Double = "System.Double";
        public const string Type_Bool = "System.Boolean";
        public const string Type_Char = "System.Char";
        public const string Type_Decimal = "System.Decimal";
        public const string Type_String = "System.String";
        #endregion

        public const string Type_DateTime = "System.DateTime";
        public const string Type_DateTimeOffset = "System.DateTimeOffset";

        public const string Type_JsonNode = "System.Text.Json.JsonNode";
        public const string Type_JsonObject = "System.Text.Json.JsonObject";
        public const string Type_JsonArray = "System.Text.Json.JsonArray";
        public const string Type_JsonValue = "System.Text.Json.JsonValue";

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
                switch (type.FullName)
                {
                    case Type_String:
                        return value.GetValue<string>();
                    case Type_SByte:
                        return value.GetValue<sbyte>();
                    case Type_Byte:
                        return value.GetValue<byte>();
                    case Type_Short:
                        return value.GetValue<short>();
                    case Type_UShort:
                        return value.GetValue<ushort>();
                    case Type_Int:
                        return value.GetValue<int>();
                    case Type_UInt:
                        return value.GetValue<uint>();
                    case Type_Long:
                        return value.GetValue<long>();
                    case Type_ULong:
                        return value.GetValue<ulong>();
                    case Type_Float:
                        return value.GetValue<float>();
                    case Type_Double:
                        return value.GetValue<double>();
                    case Type_Bool:
                        return value.GetValue<bool>();
                    case Type_DateTime:
                        return value.GetValue<DateTime>();
                    case Type_DateTimeOffset:
                        return value.GetValue<DateTimeOffset>();
                    case Type_Char:
                        return value.GetValue<char>();
                    case Type_Decimal:
                        return value.GetValue<decimal>();
                    default:
                        return JsonSerializer.Deserialize(value, type);
                }
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
                        return null;
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
    }
}

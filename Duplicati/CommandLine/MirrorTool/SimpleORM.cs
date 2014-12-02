﻿//  Copyright (C) 2014, Kenneth Skovhede

//  http://www.hexad.dk, opensource@hexad.dk
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using System.Linq;
using System.Collections.Generic;

namespace Duplicati.CommandLine.MirrorTool
{
    public class TablenameAttribute : Attribute
    {
        public string Tablename { get; set; }
        public TablenameAttribute(string name)
        {
            this.Tablename = name;
        }
    }

    public class IDFieldAttribute : Attribute
    {
        public string IDField { get; set; }
        public IDFieldAttribute(string name)
        {
            this.IDField = name;
        }
    }

    public class FieldnameAttribute : Attribute
    {
        public string Fieldname { get; set; }
        public bool? Autogenerated { get; set; }
        public FieldnameAttribute(string name)
        {
            this.Fieldname = name;
        }
        public FieldnameAttribute(string name, bool autogenerated)
        {
            this.Fieldname = name;
            this.Autogenerated = autogenerated;
        }
    }

    public class FieldValueConverterAttribute : Attribute
    {
        public virtual object ToManagedType(System.Reflection.PropertyInfo prop, object o)
        {
            if (prop.PropertyType.IsEnum)
                return DataConverters.ConvertToEnum(prop.PropertyType, o, Enum.GetValues(prop.PropertyType).GetValue(0));
            else if (prop.PropertyType == typeof(string))
                return DataConverters.ConvertToString(o);
            else if (prop.PropertyType == typeof(long))
                return DataConverters.ConvertToInt64(o);
            else if (prop.PropertyType == typeof(bool))
                return DataConverters.ConvertToBoolean(o);
            else if (prop.PropertyType == typeof(DateTime))
                return DataConverters.ConvertToDateTime(o);
            else
                return o;
        }

        public virtual object ToDatabaseType(System.Reflection.PropertyInfo prop, object o)
        {
            if (prop.PropertyType.IsEnum)
                return o.ToString();
            else if (prop.PropertyType == typeof(DateTime))
                return DataConverters.NormalizeDateTimeToEpochSeconds((DateTime)o);
            else
                return o;
        }
    }

    public static class DataConverters
    {
        /// <summary>
        /// Normalizes a DateTime instance floor'ed to seconds and in UTC
        /// </summary>
        /// <returns>The normalised date time</returns>
        /// <param name="input">The input time</param>
        public static DateTime NormalizeDateTime(DateTime input)
        {
            var ticks = input.ToUniversalTime().Ticks;
            ticks -= ticks % TimeSpan.TicksPerSecond;
            return new DateTime(ticks, DateTimeKind.Utc);
        }
        
        public static long NormalizeDateTimeToEpochSeconds(DateTime input)
        {
            return (long)Math.Floor((NormalizeDateTime(input) - Library.Utility.Utility.EPOCH).TotalSeconds);
        }
        
        public static DateTime ConvertToDateTime(object r)
        {
            var unixTime = ConvertToInt64(r);
            if (unixTime == 0)
                return new DateTime(0);
            
            return Library.Utility.Utility.EPOCH.AddSeconds(unixTime);
        }
        
        public static bool ConvertToBoolean(object r)
        {
            return ConvertToInt64(r) == 1;
        }
        
        public static string ConvertToString(object r)
        {
            if (r == null || r == DBNull.Value)
                return null;
            else
                return r.ToString();
        }
        
        public static long ConvertToInt64(object r, long @default = 0)
        {
            if (r == null || r == DBNull.Value)
                return @default;
            else
                return Convert.ToInt64(r);
        }

        public static T ConvertToEnum<T>(object r, T @default)
            where T : struct
        {
            T res;
            if (!Enum.TryParse<T>(ConvertToString(r), true, out res))
                return @default;
            return res;
        }

        public static object ConvertToEnum(Type enumType, object r, object @default)
        {
            try
            {
                return Enum.Parse(enumType, ConvertToString(r));
            }
            catch
            {
            }

            return @default;
        }
    }


    public class SimpleORM
    {
        private System.Data.IDbConnection m_connection;

        public SimpleORM(System.Data.IDbConnection connection)
        {
            m_connection = connection;
        }
            
        #region Reflection mapping

        private string GetTablename<T>()
        {
            var cust = typeof(T).GetCustomAttributes(typeof(TablenameAttribute), false).FirstOrDefault() as TablenameAttribute;
            return (cust == null || string.IsNullOrWhiteSpace(cust.Tablename)) ? typeof(T).Name : cust.Tablename;
        }

        private System.Reflection.PropertyInfo GetIDField<T>()
        {
            var cust = typeof(T).GetCustomAttributes(typeof(IDFieldAttribute), false).FirstOrDefault() as IDFieldAttribute;
            var name = (cust == null || string.IsNullOrWhiteSpace(cust.IDField)) ? "ID" : cust.IDField;

            return typeof(T).GetProperty(name);
        }

        private object GetIDValue<T>(T item)
        {
            return GetIDField<T>().GetValue(item, null);
        }

        private System.Reflection.PropertyInfo[] GetORMFields<T>()
        {
            var flags = 
                System.Reflection.BindingFlags.FlattenHierarchy | 
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public;

            var supportedPropertyTypes = new Type[] {
                typeof(long),
                typeof(string),
                typeof(bool),
                typeof(DateTime)
            };

            return 
                (from n in typeof(T).GetProperties(flags)
                where supportedPropertyTypes.Contains(n.PropertyType) || n.PropertyType.IsEnum
                select n).ToArray();        
        }

        private string GetFieldName(System.Reflection.PropertyInfo pi)
        {
            var cust = pi.GetCustomAttributes(typeof(FieldnameAttribute), false).FirstOrDefault() as FieldnameAttribute;
            return (cust == null || string.IsNullOrWhiteSpace(cust.Fieldname)) ? pi.Name : cust.Fieldname;
        }

        #endregion


        public bool DeleteFromDb<T>(T item, System.Data.IDbTransaction transaction = null)
        {
            return DeleteFromDb<T>(GetIDValue(item), transaction);
        }

        public bool DeleteFromDb<T>(object id, System.Data.IDbTransaction transaction = null)                        
        {
            var tablename = GetTablename<T>();

            if (transaction == null) 
            {
                using(var tr = m_connection.BeginTransaction())
                {
                    var r = DeleteFromDb(id, tr);
                    tr.Commit();
                    return r;
                }
            }
            else
            {
                using(var cmd = m_connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = string.Format(@"DELETE FROM ""{0}"" WHERE ID=?", tablename);
                    var p = cmd.CreateParameter();
                    p.Value = id;
                    cmd.Parameters.Add(p);
                    
                    var r = cmd.ExecuteNonQuery();
                    if (r > 1)
                        throw new Exception(string.Format("Too many records attempted deleted from table {0} for id {1}: {2}", tablename, id, r));
                    return r == 1;
                }
            }
        }

        public IEnumerable<T> ReadFromDb<T>(string whereclause, params object[] args)
        {
            var properties = GetORMFields<T>();

            var sql = string.Format(
                @"SELECT ""{0}"" FROM ""{1}"" {2} {3}",
                string.Join(@""", """, properties.Select(x => GetFieldName(x))),
                GetTablename<T>(),
                string.IsNullOrWhiteSpace(whereclause) ? "" : " WHERE ",
                whereclause ?? ""
            );

            return ReadFromDb((rd) => {
                    var item = Activator.CreateInstance<T>();
                    for(var i = 0; i < properties.Length; i++)
                    {
                        var prop = properties[i];
                        var obj = rd.GetValue(i);

                        var conv = (prop.GetCustomAttributes(typeof(FieldValueConverterAttribute), true).FirstOrDefault() as FieldValueConverterAttribute) ?? new FieldValueConverterAttribute();
                        prop.SetValue(item, conv.ToManagedType(prop, obj), null);
                    }

                    return item;
                }, sql, args);
        }

        public void InsertOrReplace<T>(IEnumerable<T> values, System.Data.IDbTransaction transaction = null)
        {
            UpdateOrInserOrReplace(values, false, true, transaction);
        }

        public void InsertIntoDb<T>(IEnumerable<T> values, System.Data.IDbTransaction transaction = null)
        {
            UpdateOrInserOrReplace(values, false, false, transaction);
        }

        public void UpdateDb<T>(IEnumerable<T> values, System.Data.IDbTransaction transaction = null)
        {
            UpdateOrInserOrReplace(values, true, false, transaction);
        }

        private void UpdateOrInserOrReplace<T>(IEnumerable<T> values, bool updateExisting, bool overwriteExisting, System.Data.IDbTransaction transaction = null)
        {
            var properties = GetORMFields<T>();
            var idfieldprop = GetIDField<T>();
            var idfieldname = idfieldprop == null ? null : idfieldprop.Name;
            var idfield = properties.Where(x => x.Name == idfieldname).FirstOrDefault();

            var autogen = false;
            if (idfield != null)
            {
                var fieldprop = idfield.GetCustomAttributes(typeof(FieldnameAttribute), false).FirstOrDefault() as FieldnameAttribute;
                var auto = fieldprop == null || (fieldprop != null && !fieldprop.Autogenerated.HasValue);
                autogen = 
                    (auto && GetFieldName(idfield) == "ID" && idfield.PropertyType == typeof(long))
                    || (!auto && fieldprop.Autogenerated.Value);
            }

            if (autogen)
                properties = properties.Where(x => x.Name != idfieldname).ToArray();

            string sql;
            string deleteSql = null;
            object[] deleteArgs = null;



            if (overwriteExisting)
            {
                deleteArgs = values.Select(x => GetIDValue(x)).ToArray();
                deleteSql = string.Format(
                    @"DELETE FROM ""{0}"" WHERE ""{1}"" IN ({2})",
                    GetTablename<T>(),
                    idfieldname,
                    string.Join(@", ", deleteArgs.Select(x => "?"))
                );

            }
            if (updateExisting && !overwriteExisting)
            {
                sql = string.Format(
                    @"UPDATE ""{0}"" SET {2} WHERE ""{1}""=?",
                    GetTablename<T>(),
                    idfieldname,
                    string.Join(@", ", properties.Select(x => string.Format(@"""{0}""=?", GetFieldName(x))))
                );

                properties = properties.Union(new System.Reflection.PropertyInfo[] { idfield }).ToArray();
            }
            else
            {

                sql = string.Format(
                    @"INSERT INTO ""{0}"" (""{1}"") VALUES ({2})",
                    GetTablename<T>(),
                    string.Join(@""", """, properties.Select(x => GetFieldName(x))),
                    string.Join(@", ", properties.Select(x => "?"))
                );
            }

            OverwriteAndUpdateDb(transaction, deleteSql, deleteArgs, values, sql, (item) =>
            {
                return properties.Select((x) =>
                {
                    var conv = (x.GetCustomAttributes(typeof(FieldValueConverterAttribute), true).FirstOrDefault() as FieldValueConverterAttribute) ?? new FieldValueConverterAttribute();
                    return conv.ToDatabaseType(x, x.GetValue(item, null));
                }).ToArray();
            });

            if (!updateExisting && values.Count() == 1 && idfield != null && autogen)
                using(var cmd = m_connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"SELECT last_insert_rowid();";
                    var id = cmd.ExecuteScalar();
                    var idconv = (idfield.GetCustomAttributes(typeof(FieldValueConverterAttribute), true).FirstOrDefault() as FieldValueConverterAttribute) ?? new FieldValueConverterAttribute();
                    idfield.SetValue(values.First(), idconv.ToManagedType(idfield, id), null);
                }
        }
        
        private IEnumerable<T> ReadFromDb<T>(Func<System.Data.IDataReader, T> f, string sql, params object[] args)
        {
            using(var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = sql;
                if (args != null)
                    foreach(var a in args)
                    {
                        var p = cmd.CreateParameter();
                        p.Value = a;
                        cmd.Parameters.Add(p);
                    }
                
                using(var rd = cmd.ExecuteReader())
                    while (rd.Read())
                        yield return f(rd);
            }
        }
                
        private void OverwriteAndUpdateDb<T>(System.Data.IDbTransaction transaction, string deleteSql, object[] deleteArgs, IEnumerable<T> values, string insertSql, Func<T, object[]> f)
        {
            using(var cmd = m_connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                
                if (!string.IsNullOrEmpty(deleteSql))
                {
                    cmd.CommandText = deleteSql;
                    if (deleteArgs != null)
                        foreach(var a in deleteArgs)
                        {
                            var p = cmd.CreateParameter();
                            p.Value = a;
                            cmd.Parameters.Add(p);
                        }
                
                    cmd.ExecuteNonQuery();
                    cmd.Parameters.Clear();
                }
                
                cmd.CommandText = insertSql;
                                
                foreach(var n in values)
                {
                    var r = f(n);
                    if (r == null)
                        continue;
                        
                    while (cmd.Parameters.Count < r.Length)
                        cmd.Parameters.Add(cmd.CreateParameter());
                    
                    for(var i = 0; i < r.Length; i++)
                        ((System.Data.IDbDataParameter)cmd.Parameters[i]).Value = r[i];
                            
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}

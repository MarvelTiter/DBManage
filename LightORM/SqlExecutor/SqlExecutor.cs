﻿using MDbContext.SqlExecutor.Service;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MDbContext.SqlExecutor {
    public static class SqlExecutor {

        public static int Execute(this IDbConnection self, string sql, object param = null, IDbTransaction trans = null, CommandType? commandType = CommandType.Text) {
            CommandDefinition command = new CommandDefinition(sql, param, trans, commandType);
            return self.internalExecute(command);
        }

        public static object ExecuteScale(this IDbConnection self, string sql, object param = null, IDbTransaction trans = null, CommandType? commandType = CommandType.Text) {
            CommandDefinition command = new CommandDefinition(sql, param, trans, commandType);
            return self.internalScale(command);
        }

        public static DataTable ExecuteReader(this IDbConnection self, string sql, object param = null, IDbTransaction trans = null, CommandType? commandType = CommandType.Text) {
            CommandDefinition command = new CommandDefinition(sql, param, trans, commandType);
            return self.internalExecuteReader(command);
        }

        public static IEnumerable<T> Query<T>(this IDbConnection self, string sql, object param = null, IDbTransaction trans = null, CommandType? commandType = CommandType.Text) {
            CommandDefinition command = new CommandDefinition(sql, param, trans, commandType);
            var result = self.internalQuery<T>(command);
            return result;
        }

        public static T QuerySingle<T>(this IDbConnection self, string sql, object param = null, IDbTransaction trans = null, CommandType? commandType = CommandType.Text) {
            CommandDefinition command = new CommandDefinition(sql, param, trans, commandType);
            return self.internalSingle<T>(command);
        }

        public static bool ExecuteTrans(this IDbConnection self, IList<string> sqls, IList<object> ps) {
            self.Open();
            var tran = self.BeginTransaction();
            try {
                if (sqls.Count != ps.Count)
                    throw new ArgumentException("SQL与参数不匹配");
                for (int i = 0; i < sqls.Count; i++) {
                    var sql = sqls[i];
                    var p = ps[i];
                    self.Execute(sql, p, tran);
                }
                tran.Commit();
                return true;
            } catch (Exception ex) {
                tran.Rollback();
                self.Close();
                throw ex;
            } finally {
                self.Close();
            }
        }

        private static IEnumerable<T> internalQuery<T>(this IDbConnection conn, CommandDefinition command) {
            // 缓存
            var parameter = command.Parameters;
            Certificate certificate = new Certificate(command.CommandText, command.CommandType, conn, typeof(T), parameter?.GetType());
            CacheInfo cacheInfo = CacheInfo.GetCacheInfo(certificate, parameter);
            // 读取
            IDbCommand cmd = null;
            IDataReader reader = null;
            var wasClosed = conn.State == ConnectionState.Closed;
            try {
                cmd = command.SetupCommand(conn, cacheInfo.ParameterReader);
                if (wasClosed)
                    conn.Open();
                reader = ExecuteReaderWithFlagsFallback(cmd, wasClosed, CommandBehavior.SingleResult);
                if (cacheInfo.Deserializer == null) {
                    cacheInfo.Deserializer = BuildDeserializer<T>(reader);
                }
                while (reader.Read()) {
                    var val = cacheInfo.Deserializer(reader);
                    yield return GetValue<T>(val);
                }
            } finally {
                // dispose
                if (reader != null) {
                    if (!reader.IsClosed) {
                        try {
                            cmd.Cancel();
                        } catch {
                        }
                    }
                    reader.Dispose();
                }
                if (wasClosed) {
                    conn.Close();
                }
                cmd?.Dispose();
            }

            IDataReader ExecuteReaderWithFlagsFallback(IDbCommand c, bool close, CommandBehavior behavior) {
                try {
                    return c.ExecuteReader(GetBehavior(close, behavior));
                } catch (ArgumentException ex) {
                    throw;
                }
            }
            CommandBehavior GetBehavior(bool close, CommandBehavior @default) {
                return (close ? (@default | CommandBehavior.CloseConnection) : @default);
            }
        }

        private static T internalSingle<T>(this IDbConnection conn, CommandDefinition command) {
            return internalReader(conn, command, (reader, cacheInfo) => {
                while (reader.Read()) {
                    var val = cacheInfo.Deserializer(reader);
                    return GetValue<T>(val);
                }
                return default;
            });
        }

        private static DataTable internalExecuteReader(this IDbConnection conn, CommandDefinition command) {
            return internalReader(conn, command, (reader, cacheInfo) => {
                DataTable dt = new DataTable();
                dt.Load(reader);
                return dt;
            });
        }

        private static object internalScale(this IDbConnection conn, CommandDefinition command) {
            return internalExector(conn, command, cmd => {
                var obj = cmd.ExecuteScalar();
                return obj;
            });
        }

        private static int internalExecute(this IDbConnection conn, CommandDefinition command) {
            return internalExector(conn, command, cmd => {
                var count = cmd.ExecuteNonQuery();
                return count;
            });
        }

        private static T internalReader<T>(IDbConnection conn, CommandDefinition command, Func<IDataReader, CacheInfo, T> func) {
            // 缓存
            var parameter = command.Parameters;
            Certificate certificate = new Certificate(command.CommandText, command.CommandType, conn, typeof(T), parameter?.GetType());
            CacheInfo cacheInfo = CacheInfo.GetCacheInfo(certificate, parameter);
            // 读取
            var wasClosed = conn.State == ConnectionState.Closed;
            IDbCommand cmd = null;
            IDataReader reader = null;
            try {
                if (wasClosed)
                    conn.Open();
                cmd = command.SetupCommand(conn, cacheInfo.ParameterReader);
                reader = cmd.ExecuteReader(CommandBehavior.SingleResult | CommandBehavior.SequentialAccess);
                if (cacheInfo.Deserializer == null) {
                    cacheInfo.Deserializer = BuildDeserializer<T>(reader);
                }
                return func(reader, cacheInfo);
            } finally {
                // dispose
                if (reader != null) {
                    if (!reader.IsClosed) {
                        try {
                            cmd.Cancel();
                        } catch {
                        }
                    }
                    reader.Dispose();
                }
                if (wasClosed) {
                    conn.Close();
                }
                cmd?.Dispose();
            }
        }
        private static T internalExector<T>(IDbConnection conn, CommandDefinition command, Func<IDbCommand, T> func) {
            // 缓存
            var parameter = command.Parameters;
            Certificate certificate = new Certificate(command.CommandText, command.CommandType, conn, typeof(object), parameter?.GetType());
            CacheInfo cacheInfo = CacheInfo.GetCacheInfo(certificate, parameter);
            // 读取
            var wasClosed = conn.State == ConnectionState.Closed;
            IDbCommand cmd = null;
            try {
                if (wasClosed)
                    conn.Open();
                cmd = command.SetupCommand(conn, cacheInfo.ParameterReader);
                return func(cmd);
            } finally {
                // dispose               
                if (wasClosed) {
                    conn.Close();
                }
                cmd?.Dispose();
            }
        }
        private static Func<IDataReader, object> BuildDeserializer<T>(IDataReader reader) {
            IDeserializer des = new ExpressionBuilder();
            return des.BuildDeserializer<T>(reader);
        }
        private static T GetValue<T>(object val) {
            if (val is T) {
                return (T)val;
            }
            Type effectiveType = typeof(T);
            if (val == null && (!effectiveType.IsValueType || Nullable.GetUnderlyingType(effectiveType) != null)) {
                return default(T);
            }

            try {
                Type conversionType = Nullable.GetUnderlyingType(effectiveType) ?? effectiveType;
                return (T)Convert.ChangeType(val, conversionType, CultureInfo.InvariantCulture);
            } catch (Exception ex) {
                return default(T);
            }
        }
    }
}

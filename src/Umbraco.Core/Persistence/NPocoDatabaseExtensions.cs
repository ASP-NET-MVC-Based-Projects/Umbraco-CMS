﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Data.SqlServerCe;
using System.Linq;
using System.Text.RegularExpressions;
using NPoco;
using StackExchange.Profiling.Data;
using Umbraco.Core.Persistence.FaultHandling;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Persistence
{
    /// <summary>
    /// Provides extension methods to NPoco Database class.
    /// </summary>
    public static partial class NPocoDatabaseExtensions
    {
        // NOTE
        //
        // proper way to do it with TSQL and SQLCE
        //   IF EXISTS (SELECT ... FROM table WITH (UPDLOCK,HOLDLOCK)) WHERE ...)
        //   BEGIN
        //     UPDATE table SET ... WHERE ...
        //   END
        //   ELSE
        //   BEGIN
        //     INSERT INTO table (...) VALUES (...)
        //   END
        //
        // works in READ COMMITED, TSQL & SQLCE lock the constraint even if it does not exist, so INSERT is OK
        //
        // proper way to do it with MySQL
        //   IF EXISTS (SELECT ... FROM table WHERE ... FOR UPDATE)
        //   BEGIN
        //     UPDATE table SET ... WHERE ...
        //   END
        //   ELSE
        //   BEGIN
        //     INSERT INTO table (...) VALUES (...)
        //   END
        //
        // MySQL locks the constraint ONLY if it exists, so INSERT may fail...
        //   in theory, happens in READ COMMITTED but not REPEATABLE READ
        //   http://www.percona.com/blog/2012/08/28/differences-between-read-committed-and-repeatable-read-transaction-isolation-levels/
        //   but according to
        //   http://dev.mysql.com/doc/refman/5.0/en/set-transaction.html
        //   it won't work for exact index value (only ranges) so really...
        //
        // MySQL should do
        //   INSERT INTO table (...) VALUES (...) ON DUPLICATE KEY UPDATE ...
        //
        // also the lock is released when the transaction is committed
        // not sure if that can have unexpected consequences on our code?
        //
        // so... for the time being, let's do with that somewhat crazy solution below...
        // todo: use the proper database syntax, not this kludge

        /// <summary>
        /// Safely inserts a record, or updates if it exists, based on a unique constraint.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="poco"></param>
        /// <returns>The action that executed, either an insert or an update. If an insert occurred and a PK value got generated, the poco object
        /// passed in will contain the updated value.</returns>
        /// <remarks>
        /// <para>We cannot rely on database-specific options such as MySql ON DUPLICATE KEY UPDATE or MSSQL MERGE WHEN MATCHED because SQLCE
        /// does not support any of them. Ideally this should be achieved with proper transaction isolation levels but that would mean revisiting
        /// isolation levels globally. We want to keep it simple for the time being and manage it manually.</para>
        /// <para>We handle it by trying to update, then insert, etc. until something works, or we get bored.</para>
        /// <para>Note that with proper transactions, if T2 begins after T1 then we are sure that the database will contain T2's value
        /// once T1 and T2 have completed. Whereas here, it could contain T1's value.</para>
        /// </remarks>
        internal static RecordPersistenceType InsertOrUpdate<T>(this IUmbracoDatabase db, T poco)
            where T : class
        {
            return db.InsertOrUpdate(poco, null, null);
        }

        /// <summary>
        /// Safely inserts a record, or updates if it exists, based on a unique constraint.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="poco"></param>
        /// <param name="updateArgs"></param>
        /// <param name="updateCommand">If the entity has a composite key they you need to specify the update command explicitly</param>
        /// <returns>The action that executed, either an insert or an update. If an insert occurred and a PK value got generated, the poco object
        /// passed in will contain the updated value.</returns>
        /// <remarks>
        /// <para>We cannot rely on database-specific options such as MySql ON DUPLICATE KEY UPDATE or MSSQL MERGE WHEN MATCHED because SQLCE
        /// does not support any of them. Ideally this should be achieved with proper transaction isolation levels but that would mean revisiting
        /// isolation levels globally. We want to keep it simple for the time being and manage it manually.</para>
        /// <para>We handle it by trying to update, then insert, etc. until something works, or we get bored.</para>
        /// <para>Note that with proper transactions, if T2 begins after T1 then we are sure that the database will contain T2's value
        /// once T1 and T2 have completed. Whereas here, it could contain T1's value.</para>
        /// </remarks>
        internal static RecordPersistenceType InsertOrUpdate<T>(this IUmbracoDatabase db,
            T poco,
            string updateCommand,
            object updateArgs)
            where T : class
        {
            if (poco == null)
                throw new ArgumentNullException(nameof(poco));

            // todo - NPoco has a Save method that works with the primary key
            //  in any case, no point trying to update if there's no primary key!

            // try to update
            var rowCount = updateCommand.IsNullOrWhiteSpace()
                    ? db.Update(poco)
                    : db.Update<T>(updateCommand, updateArgs);
            if (rowCount > 0)
                return RecordPersistenceType.Update;

            // failed: does not exist, need to insert
            // RC1 race cond here: another thread may insert a record with the same constraint

            var i = 0;
            while (i++ < 4)
            {
                try
                {
                    // try to insert
                    db.Insert(poco);
                    return RecordPersistenceType.Insert;
                }
                catch (SqlException) // assuming all db engines will throw that exception
                {
                    // failed: exists (due to race cond RC1)
                    // RC2 race cond here: another thread may remove the record

                    // try to update
                    rowCount = updateCommand.IsNullOrWhiteSpace()
                        ? db.Update(poco)
                        : db.Update<T>(updateCommand, updateArgs);
                    if (rowCount > 0)
                        return RecordPersistenceType.Update;

                    // failed: does not exist (due to race cond RC2), need to insert
                    // loop
                }
            }

            // this can go on forever... have to break at some point and report an error.
            throw new DataException("Record could not be inserted or updated.");
        }

        /// <summary>
        /// This will escape single @ symbols for npoco values so it doesn't think it's a parameter
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string EscapeAtSymbols(string value)
        {
            if (value.Contains("@") == false) return value;

            //this fancy regex will only match a single @ not a double, etc...
            var regex = new Regex("(?<!@)@(?!@)");
            return regex.Replace(value, "@@");
        }

        /// <summary>
        /// Returns the underlying connection as a typed connection - this is used to unwrap the profiled mini profiler stuff
        /// </summary>
        /// <typeparam name="TConnection"></typeparam>
        /// <param name="connection"></param>
        /// <returns></returns>
        private static TConnection GetTypedConnection<TConnection>(IDbConnection connection)
            where TConnection : class, IDbConnection
        {
            var profiled = connection as ProfiledDbConnection;
            return profiled == null ? connection as TConnection : profiled.InnerConnection as TConnection;
        }

        /// <summary>
        /// Returns the underlying transaction as a typed transaction - this is used to unwrap the profiled mini profiler stuff
        /// </summary>
        /// <typeparam name="TTransaction"></typeparam>
        /// <param name="transaction"></param>
        /// <returns></returns>
        private static TTransaction GetTypedTransaction<TTransaction>(IDbTransaction transaction)
            where TTransaction : class, IDbTransaction
        {
            var profiled = transaction as ProfiledDbTransaction;
            return profiled == null ? transaction as TTransaction : profiled.WrappedTransaction as TTransaction;
        }

        /// <summary>
        /// Returns the underlying command as a typed command - this is used to unwrap the profiled mini profiler stuff
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="command"></param>
        /// <returns></returns>
        private static TCommand GetTypedCommand<TCommand>(IDbCommand command)
            where TCommand : class, IDbCommand
        {
            var faultHandling = command as FaultHandlingDbCommand;
            if (faultHandling != null) command = faultHandling.Inner;
            var profiled = command as ProfiledDbCommand;
            if (profiled != null) command = profiled.InternalCommand;
            return command as TCommand;
        }

        public static void TruncateTable(this IDatabase db, ISqlSyntaxProvider sqlSyntax, string tableName)
        {
            var sql = new Sql(string.Format(
                sqlSyntax.TruncateTable,
                sqlSyntax.GetQuotedTableName(tableName)));
            db.Execute(sql);
        }

        public static IsolationLevel GetCurrentTransactionIsolationLevel(this IDatabase database)
        {
            var transaction = database.Transaction;
            return transaction?.IsolationLevel ?? IsolationLevel.Unspecified;
        }

        public static IEnumerable<TResult> FetchByGroups<TResult, TSource>(this IDatabase db, IEnumerable<TSource> source, int groupSize, Func<IEnumerable<TSource>, Sql<ISqlContext>> sqlFactory)
        {
            return source.SelectByGroups(x => db.Fetch<TResult>(sqlFactory(x)), groupSize);
        }
    }
}

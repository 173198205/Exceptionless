﻿#region Copyright 2014 Exceptionless

// This program is free software: you can redistribute it and/or modify it 
// under the terms of the GNU Affero General Public License as published 
// by the Free Software Foundation, either version 3 of the License, or 
// (at your option) any later version.
// 
//     http://www.gnu.org/licenses/agpl-3.0.html

#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeSmith.Core.Extensions;
using CodeSmith.Core.Helpers;
using Exceptionless;
using Exceptionless.Logging;
using Exceptionless.Models;
using Exceptionless.Serialization;
using Exceptionless.Extensions;

namespace SampleConsole {
    internal class Program {
        private static readonly Random _random = new Random();
        private static bool _sendingContinuous = false;

        private static void Main() {
            ExceptionlessClient.Current.Log = new TraceExceptionlessLog();
            ExceptionlessClient.Current.SuspendProcessing(TimeSpan.FromMinutes(1));
            ExceptionlessClient.Current.Startup();
            var tokenSource = new CancellationTokenSource();
            CancellationToken token = tokenSource.Token;
            int errorCode = _random.Next();

            while (true) {
                if (!_sendingContinuous) {
                    Console.Clear();
                    Console.WriteLine("1: Send 1\r\n2: Send 100\r\n3: Send 1 per second\r\n4: Send 10 per second\r\n5: Send 1,000\r\n6: Process queue\r\n7: Process directory\r\n\r\nQ: Quit");
                }

                ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                Trace.WriteLine(String.Format("Key {0} pressed.", keyInfo.Key));

                if (keyInfo.Key == ConsoleKey.D1)
                    SendError(errorCode: errorCode);
                if (keyInfo.Key == ConsoleKey.D2)
                    SendContinuousErrors(50, token, randomizeDates: true, maxErrors: 100, uniqueCount: 25);
                else if (keyInfo.Key == ConsoleKey.D3)
                    SendContinuousErrors(1000, token, randomizeDates: true, uniqueCount: 5, maxDaysOld: 1);
                else if (keyInfo.Key == ConsoleKey.D4)
                    SendContinuousErrors(50, token, randomizeDates: true, uniqueCount: 5);
                else if (keyInfo.Key == ConsoleKey.D5)
                    SendContinuousErrors(50, token, randomizeDates: true, maxErrors: 1000, uniqueCount: 25);
                else if (keyInfo.Key == ConsoleKey.D6)
                    ExceptionlessClient.Current.ProcessQueue();
                else if (keyInfo.Key == ConsoleKey.D7)
                    SendAllCapturedErrorsFromDisk();
                else if (keyInfo.Key == ConsoleKey.Q)
                    break;
                else if (keyInfo.Key == ConsoleKey.S) {
                    tokenSource.Cancel();
                    tokenSource = new CancellationTokenSource();
                    token = tokenSource.Token;
                }

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        private static void SendContinuousErrors(int delay, CancellationToken token, bool randomizeDates = false, int maxErrors = Int32.MaxValue, int uniqueCount = 1, bool randomizeCritical = true, int maxDaysOld = 90) {
            _sendingContinuous = true;
            Console.WriteLine();
            Console.WriteLine("Press 's' to stop sending.");
            int errorCount = 0;
            if (uniqueCount <= 0)
                uniqueCount = 1;

            var errorCodeList = new List<int>();
            for (int i = 0; i < uniqueCount; i++)
                errorCodeList.Add(_random.Next());

            Task.Factory.StartNew(delegate {
                while (errorCount < maxErrors) {
                    if (token.IsCancellationRequested) {
                        _sendingContinuous = false;
                        break;
                    }

                    SendError(randomizeDates, errorCodeList.Random(), randomizeCritical ? RandomHelper.GetBool() : false, writeToConsole: false, maxDaysOld: maxDaysOld);
                    errorCount++;

                    Console.SetCursorPosition(0, 13);
                    Console.WriteLine("Sent {0} errors.", errorCount);
                    Trace.WriteLine(String.Format("Sent {0} errors.", errorCount));

                    Thread.Sleep(delay);
                }
            }, token);
        }

        private static void SendError(bool randomizeDates = false, int? errorCode = null, bool critical = false, bool writeToConsole = true, int maxDaysOld = 90) {
            if (!errorCode.HasValue)
                errorCode = _random.Next();

            try {
                throw new MyException(errorCode.Value, Guid.NewGuid().ToString());
            } catch (Exception ex) {
                ErrorBuilder err = ex.ToExceptionless()
                    .AddObject(new {
                        myApplicationVersion = new Version(1, 0),
                        Date = DateTime.Now,
                        __sessionId = "9C72E4E8-20A2-469B-AFB9-492B6E349B23",
                        SomeField10 = "testing"
                    }, "Object From Code");
                if (randomizeDates)
                    err.Target.OccurrenceDate = RandomHelper.GetDateTime(minimum: DateTime.Now.AddDays(-maxDaysOld), maximum: DateTime.Now);
                if (critical)
                    err.MarkAsCritical();
                if (ExceptionlessClient.Current.Configuration.GetBoolean("IncludeConditionalData"))
                    err.AddObject(new { Total = 32.34, ItemCount = 2, Email = "someone@somewhere.com" }, "Conditional Data");
                err.Submit();
            }

            if (writeToConsole) {
                Console.SetCursorPosition(0, 11);
                Console.WriteLine("Sent 1 error.");
                Trace.WriteLine("Sent 1 error.");
            }
        }

        private static void SendAllCapturedErrorsFromDisk() {
            string path = Path.GetFullPath(@"..\..\Errors\");
            if (!Directory.Exists(path))
                return;

            foreach (string file in Directory.GetFiles(path)) {
                var error = ModelSerializer.Current.Deserialize<Error>(file);
                ExceptionlessClient.Current.SubmitError(error);
            }
        }

        public class MyException : ApplicationException {
            public MyException(int code, string message) : base(message) {
                ErrorCode = code;
            }

            public int ErrorCode { get; set; }
        }
    }
}
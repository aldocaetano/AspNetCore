﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.CommandLine.CompilerServerLogger;

namespace Microsoft.AspNetCore.Razor.Tools
{
    internal static class ServerConnection
    {
        private const string ServerName = "rzc.dll";

        // Spend up to 1s connecting to existing process (existing processes should be always responsive).
        private const int TimeOutMsExistingProcess = 1000;

        // Spend up to 20s connecting to a new process, to allow time for it to start.
        private const int TimeOutMsNewProcess = 20000;

        public static bool WasServerMutexOpen(string mutexName)
        {
            var open = Mutex.TryOpenExisting(mutexName, out var mutex);
            if (open)
            {
                mutex.Dispose();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the value of the temporary path for the current environment assuming the working directory
        /// is <paramref name="workingDir"/>.  This function must emulate <see cref="Path.GetTempPath"/> as 
        /// closely as possible.
        /// </summary>
        public static string GetTempPath(string workingDir)
        {
            if (PlatformInformation.IsUnix)
            {
                // Unix temp path is fine: it does not use the working directory
                // (it uses ${TMPDIR} if set, otherwise, it returns /tmp)
                return Path.GetTempPath();
            }

            var tmp = Environment.GetEnvironmentVariable("TMP");
            if (Path.IsPathRooted(tmp))
            {
                return tmp;
            }

            var temp = Environment.GetEnvironmentVariable("TEMP");
            if (Path.IsPathRooted(temp))
            {
                return temp;
            }

            if (!string.IsNullOrEmpty(workingDir))
            {
                if (!string.IsNullOrEmpty(tmp))
                {
                    return Path.Combine(workingDir, tmp);
                }

                if (!string.IsNullOrEmpty(temp))
                {
                    return Path.Combine(workingDir, temp);
                }
            }

            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (Path.IsPathRooted(userProfile))
            {
                return userProfile;
            }

            return Environment.GetEnvironmentVariable("SYSTEMROOT");
        }

        public static Task<ServerResponse> RunOnServer(
            string pipeName,
            IList<string> arguments,
            ServerPaths buildPaths,
            CancellationToken cancellationToken,
            string keepAlive = null,
            bool debug = false)
        {
            if (string.IsNullOrEmpty(pipeName))
            {
                pipeName = PipeName.ComputeDefault();
            }

            return RunOnServerCore(
                arguments,
                buildPaths,
                pipeName: pipeName,
                keepAlive: keepAlive,
                timeoutOverride: null,
                tryCreateServerFunc: TryCreateServerCore,
                cancellationToken: cancellationToken,
                debug: debug);
        }

        private static async Task<ServerResponse> RunOnServerCore(
            IList<string> arguments,
            ServerPaths buildPaths,
            string pipeName,
            string keepAlive,
            int? timeoutOverride,
            Func<string, string, bool, bool> tryCreateServerFunc,
            CancellationToken cancellationToken,
            bool debug)
        {
            if (pipeName == null)
            {
                return new RejectedServerResponse();
            }

            if (buildPaths.TempDirectory == null)
            {
                return new RejectedServerResponse();
            }

            var clientDir = buildPaths.ClientDirectory;
            var timeoutNewProcess = timeoutOverride ?? TimeOutMsNewProcess;
            var timeoutExistingProcess = timeoutOverride ?? TimeOutMsExistingProcess;
            var clientMutexName = MutexName.GetClientMutexName(pipeName);
            Task<Client> pipeTask = null;
            using (var clientMutex = new Mutex(initiallyOwned: true, name: clientMutexName, createdNew: out var holdsMutex))
            {
                try
                {
                    if (!holdsMutex)
                    {
                        try
                        {
                            holdsMutex = clientMutex.WaitOne(timeoutNewProcess);

                            if (!holdsMutex)
                            {
                                return new RejectedServerResponse();
                            }
                        }
                        catch (AbandonedMutexException)
                        {
                            holdsMutex = true;
                        }
                    }

                    // Check for an already running server
                    var serverMutexName = MutexName.GetServerMutexName(pipeName);
                    var wasServerRunning = WasServerMutexOpen(serverMutexName);
                    var timeout = wasServerRunning ? timeoutExistingProcess : timeoutNewProcess;

                    if (wasServerRunning || tryCreateServerFunc(clientDir, pipeName, debug))
                    {
                        pipeTask = Client.ConnectAsync(pipeName, TimeSpan.FromMilliseconds(timeout), cancellationToken);
                    }
                }
                finally
                {
                    if (holdsMutex)
                    {
                        clientMutex.ReleaseMutex();
                    }
                }
            }

            if (pipeTask != null)
            {
                var client = await pipeTask.ConfigureAwait(false);
                if (client != null)
                {
                    var request = ServerRequest.Create(
                        buildPaths.WorkingDirectory,
                        buildPaths.TempDirectory,
                        arguments,
                        keepAlive);

                    return await TryProcessRequest(client, request, cancellationToken).ConfigureAwait(false);
                }
            }

            return new RejectedServerResponse();
        }

        /// <summary>
        /// Try to process the request using the server. Returns a null-containing Task if a response
        /// from the server cannot be retrieved.
        /// </summary>
        private static async Task<ServerResponse> TryProcessRequest(
            Client client,
            ServerRequest request,
            CancellationToken cancellationToken)
        {
            ServerResponse response;
            using (client)
            {
                // Write the request
                try
                {
                    Log("Begin writing request");
                    await request.WriteAsync(client.Stream, cancellationToken).ConfigureAwait(false);
                    Log("End writing request");
                }
                catch (Exception e)
                {
                    LogException(e, "Error writing build request.");
                    return new RejectedServerResponse();
                }

                // Wait for the compilation and a monitor to detect if the server disconnects
                var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                Log("Begin reading response");

                var responseTask = ServerResponse.ReadAsync(client.Stream, serverCts.Token);
                var monitorTask = client.WaitForDisconnectAsync(serverCts.Token);
                await Task.WhenAny(responseTask, monitorTask).ConfigureAwait(false);

                Log("End reading response");

                if (responseTask.IsCompleted)
                {
                    // await the task to log any exceptions
                    try
                    {
                        response = await responseTask.ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        LogException(e, "Error reading response");
                        response = new RejectedServerResponse();
                    }
                }
                else
                {
                    Log("Server disconnect");
                    response = new RejectedServerResponse();
                }

                // Cancel whatever task is still around
                serverCts.Cancel();
                Debug.Assert(response != null);
                return response;
            }
        }

        // Internal for testing.
        internal static bool TryCreateServerCore(string clientDir, string pipeName, bool debug = false)
        {
            string expectedPath;
            string processArguments;

            // The server should be in the same directory as the client
            var expectedCompilerPath = Path.Combine(clientDir, ServerName);
            expectedPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            processArguments = $@"""{expectedCompilerPath}"" {(debug ? "--debug" : "")} server -p {pipeName}";

            if (!File.Exists(expectedCompilerPath))
            {
                return false;
            }

            if (PlatformInformation.IsWindows)
            {
                // As far as I can tell, there isn't a way to use the Process class to
                // create a process with no stdin/stdout/stderr, so we use P/Invoke.
                // This code was taken from MSBuild task starting code.

                var startInfo = new STARTUPINFO();
                startInfo.cb = Marshal.SizeOf(startInfo);
                startInfo.hStdError = NativeMethods.InvalidIntPtr;
                startInfo.hStdInput = NativeMethods.InvalidIntPtr;
                startInfo.hStdOutput = NativeMethods.InvalidIntPtr;
                startInfo.dwFlags = NativeMethods.STARTF_USESTDHANDLES;
                var dwCreationFlags = NativeMethods.NORMAL_PRIORITY_CLASS | NativeMethods.CREATE_NO_WINDOW;

                Log("Attempting to create process '{0}'", expectedPath);

                var builder = new StringBuilder($@"""{expectedPath}"" {processArguments}");

                var success = NativeMethods.CreateProcess(
                    lpApplicationName: null,
                    lpCommandLine: builder,
                    lpProcessAttributes: NativeMethods.NullPtr,
                    lpThreadAttributes: NativeMethods.NullPtr,
                    bInheritHandles: false,
                    dwCreationFlags: dwCreationFlags,
                    lpEnvironment: NativeMethods.NullPtr, // Inherit environment
                    lpCurrentDirectory: clientDir,
                    lpStartupInfo: ref startInfo,
                    lpProcessInformation: out var processInfo);

                if (success)
                {
                    Log("Successfully created process with process id {0}", processInfo.dwProcessId);
                    NativeMethods.CloseHandle(processInfo.hProcess);
                    NativeMethods.CloseHandle(processInfo.hThread);
                }
                else
                {
                    Log("Failed to create process. GetLastError={0}", Marshal.GetLastWin32Error());
                }
                return success;
            }
            else
            {
                try
                {
                    var startInfo = new ProcessStartInfo()
                    {
                        FileName = expectedPath,
                        Arguments = processArguments,
                        UseShellExecute = false,
                        WorkingDirectory = clientDir,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    Process.Start(startInfo);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
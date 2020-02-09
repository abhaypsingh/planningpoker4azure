﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Duracellko.PlanningPoker.Service;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Duracellko.PlanningPoker.Client.Service
{
    /// <summary>
    /// Objects provides operations of Planning Poker service using SignalR.
    /// </summary>
    public sealed class PlanningPokerSignalRClient : IPlanningPokerClient, IDisposable
    {
        private const string ServiceUri = "signalr/PlanningPoker";

        private static TimeSpan[] _reconnectDelays = new[]
        {
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)
        };

        private readonly IPlanningPokerUriProvider _uriProvider;

        private HubConnection _hubConnection;
        private bool _disposed;

        private object _reconnectingLock = new object();
        private TaskCompletionSource<bool> _reconnectingTask;

        private object _getMessagesLock = new object();
        private TaskCompletionSource<IList<Message>> _getMessagesTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlanningPokerSignalRClient"/> class.
        /// </summary>
        /// <param name="uriProvider">URI provider that provides URI of Planning Poker service.</param>
        public PlanningPokerSignalRClient(IPlanningPokerUriProvider uriProvider)
        {
            _uriProvider = uriProvider ?? throw new ArgumentNullException(nameof(uriProvider));
        }

        /// <summary>
        /// Creates new Scrum team with specified team name and Scrum master name.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="scrumMasterName">Name of the Scrum master.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// Created Scrum team.
        /// </returns>
        public Task<ScrumTeam> CreateTeam(string teamName, string scrumMasterName, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                await EnsureConnected(cancellationToken);
                var result = await _hubConnection.InvokeAsync<ScrumTeam>("CreateTeam", teamName, scrumMasterName, cancellationToken);

                ConvertScrumTeam(result);
                return result;
            });
        }

        /// <summary>
        /// Connects member or observer with specified name to the Scrum team with specified name.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="memberName">Name of the member or observer.</param>
        /// <param name="asObserver">If set to <c>true</c> then connects as observer; otherwise as member.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// The Scrum team the member or observer joined to.
        /// </returns>
        public Task<ScrumTeam> JoinTeam(string teamName, string memberName, bool asObserver, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                await EnsureConnected(cancellationToken);
                var result = await _hubConnection.InvokeAsync<ScrumTeam>("JoinTeam", teamName, memberName, asObserver, cancellationToken);

                ConvertScrumTeam(result);
                return result;
            });
        }

        /// <summary>
        /// Reconnects member with specified name to the Scrum team with specified name.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="memberName">Name of the member.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// The Scrum team the member or observer reconnected to.
        /// </returns>
        /// <remarks>
        /// This operation is used to resynchronize client and server. Current status of ScrumTeam is returned and message queue for the member is cleared.
        /// </remarks>
        public Task<ReconnectTeamResult> ReconnectTeam(string teamName, string memberName, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                await EnsureConnected(cancellationToken);
                var result = await _hubConnection.InvokeAsync<ReconnectTeamResult>("ReconnectTeam", teamName, memberName, cancellationToken);

                ConvertScrumTeam(result.ScrumTeam);
                ConvertEstimation(result.SelectedEstimation);
                return result;
            });
        }

        /// <summary>
        /// Disconnects member from the Scrum team.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="memberName">Name of the member.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// Asynchronous operation.
        /// </returns>
        public Task DisconnectTeam(string teamName, string memberName, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                await EnsureConnected(cancellationToken);
                await _hubConnection.InvokeAsync("DisconnectTeam", teamName, memberName, cancellationToken);
            });
        }

        /// <summary>
        /// Signal from Scrum master to starts the estimation.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// Asynchronous operation.
        /// </returns>
        public Task StartEstimation(string teamName, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                await EnsureConnected(cancellationToken);
                await _hubConnection.InvokeAsync("StartEstimation", teamName, cancellationToken);
            });
        }

        /// <summary>
        /// Signal from Scrum master to cancels the estimation.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// Asynchronous operation.
        /// </returns>
        public Task CancelEstimation(string teamName, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                await EnsureConnected(cancellationToken);
                await _hubConnection.InvokeAsync("CancelEstimation", teamName, cancellationToken);
            });
        }

        /// <summary>
        /// Submits the estimation for specified team member.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="memberName">Name of the member.</param>
        /// <param name="estimation">The estimation the member is submitting.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// Asynchronous operation.
        /// </returns>
        public Task SubmitEstimation(string teamName, string memberName, double? estimation, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                double encodedEstimation = estimation ?? -1111111;
                if (double.IsPositiveInfinity(encodedEstimation))
                {
                    encodedEstimation = -1111100;
                }

                await EnsureConnected(cancellationToken);
                await _hubConnection.InvokeAsync("SubmitEstimation", teamName, memberName, encodedEstimation, cancellationToken);
            });
        }

        /// <summary>
        /// Begins to get messages of specified member asynchronously.
        /// </summary>
        /// <param name="teamName">Name of the Scrum team.</param>
        /// <param name="memberName">Name of the member.</param>
        /// <param name="lastMessageId">ID of last message the member received.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>
        /// List of messages.
        /// </returns>
        public Task<IList<Message>> GetMessages(string teamName, string memberName, long lastMessageId, CancellationToken cancellationToken)
        {
            return InvokeOperation(async () =>
            {
                await EnsureConnected(cancellationToken);

                try
                {
                    Task<IList<Message>> getMessagesTask;

                    lock (_getMessagesLock)
                    {
                        if (_getMessagesTask != null)
                        {
                            throw new InvalidOperationException("GetMessages is already in progress.");
                        }

                        _getMessagesTask = new TaskCompletionSource<IList<Message>>();
                        getMessagesTask = _getMessagesTask.Task;
                    }

                    await _hubConnection.InvokeAsync("GetMessages", teamName, memberName, lastMessageId, cancellationToken);

                    var result = await getMessagesTask;
                    ConvertMessages(result);
                    return result;
                }
                finally
                {
                    lock (_getMessagesLock)
                    {
                        _getMessagesTask?.TrySetCanceled();
                        _getMessagesTask = null;
                    }
                }
            });
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        private static async Task InvokeOperation(Func<Task> operation)
        {
            try
            {
                await operation();
            }
            catch (HubException ex)
            {
                throw new PlanningPokerException(ex.Message, ex);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                throw new PlanningPokerException(Client.Resources.PlanningPokerService_ConnectionError, ex);
            }
            catch (Exception ex)
            {
                // WASM .NET reports JSException when connection / negotiation fails.
                if (ex.Message != null && ex.Message.StartsWith("TypeError: Failed to fetch", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PlanningPokerException(Client.Resources.PlanningPokerService_ConnectionError, ex);
                }
                else
                {
                    throw new PlanningPokerException(Client.Resources.PlanningPokerService_UnexpectedError, ex);
                }
            }
        }

        private static async Task<T> InvokeOperation<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation();
            }
            catch (HubException ex)
            {
                throw new PlanningPokerException(ex.Message, ex);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                throw new PlanningPokerException(Client.Resources.PlanningPokerService_ConnectionError, ex);
            }
            catch (Exception ex)
            {
                // WASM .NET reports JSException when connection / negotiation fails.
                if (ex.Message != null && ex.Message.StartsWith("TypeError: Failed to fetch", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PlanningPokerException(Client.Resources.PlanningPokerService_ConnectionError, ex);
                }
                else
                {
                    throw new PlanningPokerException(Client.Resources.PlanningPokerService_UnexpectedError, ex);
                }
            }
        }

        private static void ConvertScrumTeam(ScrumTeam scrumTeam)
        {
            if (scrumTeam.AvailableEstimations != null)
            {
                ConvertEstimations(scrumTeam.AvailableEstimations);
            }

            if (scrumTeam.EstimationResult != null)
            {
                ConvertEstimations(scrumTeam.EstimationResult);
            }
        }

        private static void ConvertEstimations(IEnumerable<Estimation> estimations)
        {
            foreach (var estimation in estimations)
            {
                ConvertEstimation(estimation);
            }
        }

        private static void ConvertEstimations(IEnumerable<EstimationResultItem> estimationResultItems)
        {
            foreach (var estimationResultItem in estimationResultItems)
            {
                ConvertEstimation(estimationResultItem.Estimation);
            }
        }

        private static void ConvertEstimation(Estimation estimation)
        {
            if (estimation != null && estimation.Value == Estimation.PositiveInfinity)
            {
                estimation.Value = double.PositiveInfinity;
            }
        }

        private static void ConvertMessages(IEnumerable<Message> messages)
        {
            foreach (var message in messages)
            {
                ConvertMessage(message);
            }
        }

        private static void ConvertMessage(Message message)
        {
            if (message.Type == MessageType.EstimationEnded)
            {
                var estimationResultMessage = (EstimationResultMessage)message;
                ConvertEstimations(estimationResultMessage.EstimationResult);
            }
        }

        private void OnNotify(IList<Message> messages)
        {
            lock (_getMessagesLock)
            {
                _getMessagesTask?.TrySetResult(messages);
            }
        }

        private HubConnection CreateHubConnection()
        {
            return new HubConnectionBuilder()
                .WithUrl(new Uri(_uriProvider.BaseUri, ServiceUri))
                .AddNewtonsoftJsonProtocol()
                .WithAutomaticReconnect(_reconnectDelays)
                .Build();
        }

        private async Task EnsureConnected(CancellationToken cancellationToken)
        {
            CheckDisposed();

            if (_hubConnection == null)
            {
                _hubConnection = CreateHubConnection();
                _hubConnection.Closed += HubConnectionOnClosed;
                _hubConnection.Reconnecting += HubConnectionOnReconnecting;
                _hubConnection.Reconnected += HubConnectionOnReconnected;

                _hubConnection.On<IList<Message>>("Notify", OnNotify);
            }

            while (_hubConnection.State == HubConnectionState.Reconnecting)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task<bool> reconnectingTask = null;
                lock (_reconnectingLock)
                {
                    reconnectingTask = _reconnectingTask?.Task;
                }

                if (reconnectingTask != null)
                {
                    await reconnectingTask;
                }
                else
                {
                    await Task.Yield();
                }
            }

            if (_hubConnection.State == HubConnectionState.Disconnected)
            {
                await _hubConnection.StartAsync(cancellationToken);
            }
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PlanningPokerSignalRClient));
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_hubConnection != null)
                    {
                        _hubConnection.DisposeAsync().Wait();
                        _hubConnection = null;
                    }
                }

                _disposed = true;
            }
        }

        private Task HubConnectionOnClosed(Exception arg)
        {
            var exception = arg ?? new InvalidOperationException();
            lock (_reconnectingLock)
            {
                _reconnectingTask?.SetException(exception);
                _reconnectingTask = null;
            }

            lock (_getMessagesLock)
            {
                _getMessagesTask?.TrySetException(exception);
            }

            return Task.CompletedTask;
        }

        private Task HubConnectionOnReconnecting(Exception arg)
        {
            var exception = arg ?? new InvalidOperationException();
            lock (_reconnectingLock)
            {
                if (_reconnectingTask == null)
                {
                    _reconnectingTask = new TaskCompletionSource<bool>();
                }
            }

            lock (_getMessagesLock)
            {
                _getMessagesTask?.TrySetException(exception);
            }

            return Task.CompletedTask;
        }

        private Task HubConnectionOnReconnected(string arg)
        {
            lock (_reconnectingLock)
            {
                _reconnectingTask?.SetResult(true);
                _reconnectingTask = null;
            }

            return Task.CompletedTask;
        }
    }
}

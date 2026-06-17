namespace Application.IServices.IConsumers
{
    /// <summary>
    /// Marker for the HubSpot sync consumer. Registered as a singleton and started via
    /// RegisterOnMessageHandlerAndReceiveMessagesAsync() (from Prypto BaseConsumer), exactly like
    /// InstaMortgageService's IMoEngageConsumer / ISalesforceConsumer.
    /// </summary>
    public interface IHubSpotConsumer
    {
    }
}

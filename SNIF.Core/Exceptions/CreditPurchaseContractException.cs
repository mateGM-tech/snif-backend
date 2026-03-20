namespace SNIF.Core.Exceptions
{
    public sealed class CreditPurchaseContractException : InvalidOperationException
    {
        public CreditPurchaseContractException(string code, string message, int requestedAmount, string? requestedVariantId)
            : base(message)
        {
            Code = code;
            RequestedAmount = requestedAmount;
            RequestedVariantId = requestedVariantId;
        }

        public string Code { get; }

        public int RequestedAmount { get; }

        public string? RequestedVariantId { get; }
    }
}
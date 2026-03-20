namespace SNIF.Application.Services
{
    public class EmailDeliveryHealthState
    {
        private readonly object _gate = new();
        private int _consecutiveFailures;
        private DateTime? _lastFailureAtUtc;
        private string? _lastFailureReason;

        public int ConsecutiveFailures
        {
            get
            {
                lock (_gate)
                {
                    return _consecutiveFailures;
                }
            }
        }

        public DateTime? LastFailureAtUtc
        {
            get
            {
                lock (_gate)
                {
                    return _lastFailureAtUtc;
                }
            }
        }

        public string? LastFailureReason
        {
            get
            {
                lock (_gate)
                {
                    return _lastFailureReason;
                }
            }
        }

        public void RecordSuccess()
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
            }
        }

        public void RecordFailure(string? reason)
        {
            lock (_gate)
            {
                _consecutiveFailures++;
                _lastFailureAtUtc = DateTime.UtcNow;
                _lastFailureReason = reason;
            }
        }
    }
}

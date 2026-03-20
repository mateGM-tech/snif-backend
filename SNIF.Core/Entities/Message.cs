using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SNIF.Core.Entities
{
    public class Message : BaseEntity
    {
        public string Content { get; set; } = null!;
        public string SenderId { get; set; } = null!;
        public string ReceiverId { get; set; } = null!;
        public string MatchId { get; set; } = null!;
        public bool IsRead { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public DateTime? ReadAt { get; set; }

        // Attachment fields
        public string? AttachmentUrl { get; set; }
        public string? AttachmentType { get; set; } // "image", "file"
        public string? AttachmentFileName { get; set; }
        public long? AttachmentSizeBytes { get; set; }

        public virtual User Sender { get; set; } = null!;
        public virtual User Receiver { get; set; } = null!;
        public virtual Match Match { get; set; } = null!;
        public virtual ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
    }
}

// Models/Entities.cs
using System;
using System.Collections.Generic;

namespace CardManagement.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public string Role { get; set; }
        public int? CompanyId { get; set; }
        public Company? Company { get; set; }
    }

    public class Company
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Mobility_User1 { get; set; }
        public string Mobility_Pass { get; set; }
        public int ResellerId { get; set; }
        public Reseller? Reseller { get; set; }
        public ICollection<Unit>? Units { get; set; }
        public ICollection<Card>? Cards { get; set; }
    }

    public class Reseller
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public ICollection<Company>? Companies { get; set; }
    }

    public class Unit
    {
        public int Id { get; set; }
        public string Uid { get; set; }
        public string IMEI { get; set; }
        public string Name { get; set; }
        public string GroupName { get; set; }
        public string Status { get; set; }
        public bool Is_New_Device { get; set; }
        public int CompanyId { get; set; }
        public Company? Company { get; set; }
        public Device? Device { get; set; }
    }

    public class Device
    {
        public int Id { get; set; }
        public string Uid { get; set; }
        public string Name { get; set; }
        public string TrackerTypeName { get; set; }
        public int UnitId { get; set; }
        public Unit? Unit { get; set; }
        public ICollection<DeviceCard>? DeviceCards { get; set; }
        public ICollection<PollJob>? PollJobs { get; set; }
    }

    public class Card
    {
        public int Id { get; set; }
        public string Uid { get; set; }
        public string DriverID { get; set; }
        public string DisplayName { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string GroupName { get; set; }
        public string Tag_Type { get; set; }
        public string Tag_ID { get; set; }
        public bool Is_New { get; set; }
        public int CompanyId { get; set; }
        public Company? Company { get; set; }
        public ICollection<DeviceCard>? DeviceCards { get; set; }
    }

    public class DeviceCard
    {
        public int DeviceId { get; set; }
        public Device? Device { get; set; }
        public int CardId { get; set; }
        public Card? Card { get; set; }
        public DateTime LastSynced { get; set; }
    }

    public class Command
    {
        public int Id { get; set; }
        public int DeviceId { get; set; }
        public Device? Device { get; set; }
        public string ActionType { get; set; }
        public string Status { get; set; }
        public string HexData { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Tracks an in-progress background poll for a device's card list.
    /// Created when the user clicks "Get Card List", resolved when
    /// AdditionalDetails returns data newer than RequestedAt.
    /// </summary>
    public class PollJob
    {
        public int Id { get; set; }
        public int DeviceId { get; set; }
        public Device? Device { get; set; }

        /// <summary>UTC time the "Ask" command was sent. Used to detect stale vs fresh API data.</summary>
        public DateTime RequestedAt { get; set; }

        /// <summary>Pending → Completed → Failed</summary>
        public string Status { get; set; } = "Pending";

        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
using System.ComponentModel.DataAnnotations;
using NetEscapades.EnumGenerators;

namespace UnfoldedCircle.SystemMonitor.Configuration;

[EnumExtensions(IsInterceptable = true, MetadataSource = MetadataSource.DisplayAttribute)]
public enum SensorType : sbyte
{
    [Display(Name = "Memory Percentage")]
    MemoryPercentage = 1,

    [Display(Name = "Memory Details")]
    MemoryDetails,

    [Display(Name = "Swap Percentage")]
    SwapPercentage,

    [Display(Name = "Swap Details")]
    SwapDetails,

    [Display(Name = "CPU Usage Percent Last 1 Minute")]
    CpuUsagePercentLast1Minute,

    [Display(Name = "CPU Usage Percent Last 5 Minutes")]
    CpuUsagePercentLast5Minutes,

    [Display(Name = "CPU Usage Percent Last 15 Minutes")]
    CpuUsagePercentLast15Minutes,

    [Display(Name = "File System Percentage")]
    FileSystemPercentage,

    [Display(Name = "File System Details")]
    FileSystemDetails,

    [Display(Name = "Battery Percentage")]
    BatteryPercentage
}
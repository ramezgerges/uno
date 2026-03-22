using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.X11;

/// <summary>
/// Minimal Vulkan P/Invoke interop for initializing a Vulkan device
/// suitable for Skia Graphite context creation.
/// </summary>
internal static unsafe class VulkanInterop
{
	private const string LibVulkan = "libvulkan.so.1";

	// Vulkan result codes
	internal const int VK_SUCCESS = 0;

	// Vulkan structure types
	private const int VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
	private const int VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
	private const int VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO = 2;
	private const int VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 3;

	// Vulkan queue flags
	private const int VK_QUEUE_GRAPHICS_BIT = 0x00000001;

	// Vulkan API version (1.1.0)
	internal static uint VK_MAKE_API_VERSION(uint variant, uint major, uint minor, uint patch) =>
		(variant << 29) | (major << 22) | (minor << 12) | patch;

	internal static readonly uint VK_API_VERSION_1_1 = VK_MAKE_API_VERSION(0, 1, 1, 0);

	[StructLayout(LayoutKind.Sequential)]
	private struct VkApplicationInfo
	{
		public int sType;
		public IntPtr pNext;
		public IntPtr pApplicationName;
		public uint applicationVersion;
		public IntPtr pEngineName;
		public uint engineVersion;
		public uint apiVersion;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct VkInstanceCreateInfo
	{
		public int sType;
		public IntPtr pNext;
		public uint flags;
		public VkApplicationInfo* pApplicationInfo;
		public uint enabledLayerCount;
		public IntPtr ppEnabledLayerNames;
		public uint enabledExtensionCount;
		public IntPtr ppEnabledExtensionNames;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct VkDeviceQueueCreateInfo
	{
		public int sType;
		public IntPtr pNext;
		public uint flags;
		public uint queueFamilyIndex;
		public uint queueCount;
		public float* pQueuePriorities;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct VkDeviceCreateInfo
	{
		public int sType;
		public IntPtr pNext;
		public uint flags;
		public uint queueCreateInfoCount;
		public VkDeviceQueueCreateInfo* pQueueCreateInfos;
		public uint enabledLayerCount;
		public IntPtr ppEnabledLayerNames;
		public uint enabledExtensionCount;
		public IntPtr ppEnabledExtensionNames;
		public IntPtr pEnabledFeatures;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct VkQueueFamilyProperties
	{
		public uint queueFlags;
		public uint queueCount;
		public uint timestampValidBits;
		public uint minImageTransferGranularity_width;
		public uint minImageTransferGranularity_height;
		public uint minImageTransferGranularity_depth;
	}

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	private static extern int vkCreateInstance(VkInstanceCreateInfo* pCreateInfo, IntPtr pAllocator, out IntPtr pInstance);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	private static extern int vkEnumeratePhysicalDevices(IntPtr instance, out uint pPhysicalDeviceCount, IntPtr* pPhysicalDevices);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	private static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr physicalDevice, out uint pQueueFamilyPropertyCount, VkQueueFamilyProperties* pQueueFamilyProperties);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	private static extern int vkCreateDevice(IntPtr physicalDevice, VkDeviceCreateInfo* pCreateInfo, IntPtr pAllocator, out IntPtr pDevice);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	private static extern void vkGetDeviceQueue(IntPtr device, uint queueFamilyIndex, uint queueIndex, out IntPtr pQueue);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string pName);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr vkGetDeviceProcAddr(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string pName);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void vkDestroyDevice(IntPtr device, IntPtr pAllocator);

	[DllImport(LibVulkan, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void vkDestroyInstance(IntPtr instance, IntPtr pAllocator);

	/// <summary>
	/// Creates a minimal Vulkan instance, physical device, logical device, and queue
	/// suitable for Skia Graphite context creation.
	/// Returns null if Vulkan initialization fails.
	/// </summary>
	internal static VulkanContext? CreateVulkanContext()
	{
		try
		{
			// Create instance
			var appInfo = new VkApplicationInfo
			{
				sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
				apiVersion = VK_API_VERSION_1_1,
			};

			var createInfo = new VkInstanceCreateInfo
			{
				sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
				pApplicationInfo = &appInfo,
			};

			if (vkCreateInstance(&createInfo, IntPtr.Zero, out var instance) != VK_SUCCESS)
			{
				return null;
			}

			// Get first physical device
			uint deviceCount = 0;
			if (vkEnumeratePhysicalDevices(instance, out deviceCount, null) != VK_SUCCESS || deviceCount == 0)
			{
				vkDestroyInstance(instance, IntPtr.Zero);
				return null;
			}

			var physicalDevices = stackalloc IntPtr[(int)deviceCount];
			if (vkEnumeratePhysicalDevices(instance, out deviceCount, physicalDevices) != VK_SUCCESS)
			{
				vkDestroyInstance(instance, IntPtr.Zero);
				return null;
			}
			var physicalDevice = physicalDevices[0];

			// Find a graphics queue family
			uint queueFamilyCount = 0;
			vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, out queueFamilyCount, null);
			var queueFamilies = stackalloc VkQueueFamilyProperties[(int)queueFamilyCount];
			vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, out queueFamilyCount, queueFamilies);

			uint graphicsQueueIndex = uint.MaxValue;
			for (uint i = 0; i < queueFamilyCount; i++)
			{
				if ((queueFamilies[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0)
				{
					graphicsQueueIndex = i;
					break;
				}
			}

			if (graphicsQueueIndex == uint.MaxValue)
			{
				vkDestroyInstance(instance, IntPtr.Zero);
				return null;
			}

			// Create logical device
			float queuePriority = 1.0f;
			var queueCreateInfo = new VkDeviceQueueCreateInfo
			{
				sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
				queueFamilyIndex = graphicsQueueIndex,
				queueCount = 1,
				pQueuePriorities = &queuePriority,
			};

			var deviceCreateInfo = new VkDeviceCreateInfo
			{
				sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
				queueCreateInfoCount = 1,
				pQueueCreateInfos = &queueCreateInfo,
			};

			if (vkCreateDevice(physicalDevice, &deviceCreateInfo, IntPtr.Zero, out var device) != VK_SUCCESS)
			{
				vkDestroyInstance(instance, IntPtr.Zero);
				return null;
			}

			// Get queue
			vkGetDeviceQueue(device, graphicsQueueIndex, 0, out var queue);

			return new VulkanContext(instance, physicalDevice, device, queue, graphicsQueueIndex);
		}
		catch
		{
			return null;
		}
	}

	internal record VulkanContext(
		IntPtr Instance,
		IntPtr PhysicalDevice,
		IntPtr Device,
		IntPtr Queue,
		uint GraphicsQueueIndex) : IDisposable
	{
		public void Dispose()
		{
			if (Device != IntPtr.Zero)
			{
				vkDestroyDevice(Device, IntPtr.Zero);
			}
			if (Instance != IntPtr.Zero)
			{
				vkDestroyInstance(Instance, IntPtr.Zero);
			}
		}
	}
}

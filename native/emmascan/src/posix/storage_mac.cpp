// macOS: whether a disk is solid state or rotational, from the "Device Characteristics" that the storage
// driver publishes in the I/O Registry. Kept apart from posix_platform.cpp because Apple's headers bring
// in AssertMacros (check, verify, require) and MacTypes.

#define __ASSERT_MACROS_DEFINE_VERSIONS_WITHOUT_UNDERSCORES 0

#include "emmascan/emmascan.h"

#include <CoreFoundation/CoreFoundation.h>
#include <DiskArbitration/DiskArbitration.h>
#include <IOKit/IOKitLib.h>

#include <cstdint>
#include <cstring>

namespace emmascan::platform {

uint32_t mac_medium_type(const char* bsd_device) {
    const char* name = std::strncmp(bsd_device, "/dev/", 5) == 0 ? bsd_device + 5 : bsd_device;
    DASessionRef session = DASessionCreate(kCFAllocatorDefault);
    if (!session) return ES_STORAGE_UNKNOWN;
    uint32_t kind = ES_STORAGE_UNKNOWN;
    if (DADiskRef disk = DADiskCreateFromBSDName(kCFAllocatorDefault, session, name)) {
        if (io_service_t media = DADiskCopyIOMedia(disk)) {
            // An APFS volume's media sits on its container, which sits on the physical disk: search upwards.
            CFTypeRef found = IORegistryEntrySearchCFProperty(media, kIOServicePlane, CFSTR("Device Characteristics"),
                                                              kCFAllocatorDefault,
                                                              kIORegistryIterateRecursively | kIORegistryIterateParents);
            if (found) {
                if (CFGetTypeID(found) == CFDictionaryGetTypeID()) {
                    const void* medium =
                        CFDictionaryGetValue(static_cast<CFDictionaryRef>(found), CFSTR("Medium Type"));
                    if (medium && CFGetTypeID(medium) == CFStringGetTypeID()) {
                        const auto type = static_cast<CFStringRef>(medium);
                        if (CFStringCompare(type, CFSTR("Rotational"), 0) == kCFCompareEqualTo) kind = ES_STORAGE_HDD;
                        else if (CFStringCompare(type, CFSTR("Solid State"), 0) == kCFCompareEqualTo) kind = ES_STORAGE_SSD;
                    }
                }
                CFRelease(found);
            }
            IOObjectRelease(media);
        }
        CFRelease(disk);
    }
    CFRelease(session);
    return kind;
}

} // namespace emmascan::platform

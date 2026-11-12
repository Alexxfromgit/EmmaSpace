// macOS: space on a volume as Finder reports it. statfs() (and so .NET's DriveInfo) counts only blocks no
// file uses. Finder also counts purgeable data (Time Machine snapshots kept on the Mac, iCloud copies, caches),
// which macOS removes by itself when an app needs the room: NSURLVolumeAvailableCapacityForImportantUsageKey.

#include "../scan_state.h"

#import <Foundation/Foundation.h>

#include <algorithm>

namespace emmascan::platform {

es_status volume_space(std::u16string_view path, es_volume_space& space) {
    @autoreleasepool {
        NSString* p = [[NSString alloc] initWithCharacters:reinterpret_cast<const unichar*>(path.data())
                                                    length:path.size()];
        NSURL* url = [NSURL fileURLWithPath:p];
        NSError* error = nil;
        NSDictionary<NSURLResourceKey, id>* values =
            [url resourceValuesForKeys:@[ NSURLVolumeTotalCapacityKey, NSURLVolumeAvailableCapacityKey,
                                          NSURLVolumeAvailableCapacityForImportantUsageKey ]
                                 error:&error];
        if (!values) {
            if ([error.domain isEqualToString:NSCocoaErrorDomain] &&
                (error.code == NSFileReadNoSuchFileError || error.code == NSFileNoSuchFileError))
                return ES_ERR_NOT_FOUND;
            return ES_ERR_IO;
        }
        NSNumber* total = values[NSURLVolumeTotalCapacityKey];
        NSNumber* free = values[NSURLVolumeAvailableCapacityKey];
        if (![total isKindOfClass:[NSNumber class]] || ![free isKindOfClass:[NSNumber class]])
            return ES_ERR_NOT_SUPPORTED;
        space.total = total.unsignedLongLongValue;
        space.free = free.unsignedLongLongValue;
        // Missing (or 0) on volumes macOS doesn't manage this way, such as network shares and FAT drives.
        NSNumber* important = values[NSURLVolumeAvailableCapacityForImportantUsageKey];
        const long long available = [important isKindOfClass:[NSNumber class]] ? important.longLongValue : 0;
        space.available = std::max<uint64_t>(space.free, available > 0 ? static_cast<uint64_t>(available) : 0);
        return ES_OK;
    }
}

} // namespace emmascan::platform

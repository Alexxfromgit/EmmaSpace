// Moves items to the Trash through NSFileManager, so Finder's "Put Back" works.

#include "../scan_state.h"

#import <Foundation/Foundation.h>

#include <string>

namespace emmascan::platform {

es_status move_to_trash(std::u16string_view path) {
    @autoreleasepool {
        NSString* p = [[NSString alloc] initWithCharacters:reinterpret_cast<const unichar*>(path.data())
                                                    length:path.size()];
        NSURL* url = [NSURL fileURLWithPath:p];
        NSError* error = nil;
        if ([[NSFileManager defaultManager] trashItemAtURL:url resultingItemURL:nil error:&error]) return ES_OK;
        if ([error.domain isEqualToString:NSCocoaErrorDomain]) {
            switch (error.code) {
            case NSFileNoSuchFileError:
                return ES_ERR_NOT_FOUND;
            case NSFileWriteNoPermissionError:
                return ES_ERR_ACCESS_DENIED;
            case NSUserCancelledError:
                return ES_ERR_CANCELLED;
            default:
                break;
            }
        }
        return ES_ERR_IO;
    }
}

} // namespace emmascan::platform

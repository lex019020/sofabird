# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class dev.vlcremote.**$$serializer { *; }
-keepclassmembers class dev.vlcremote.** { *** Companion; }
-keepclasseswithmembers class dev.vlcremote.** { kotlinx.serialization.KSerializer serializer(...); }

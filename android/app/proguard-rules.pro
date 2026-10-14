# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class app.sofabird.**$$serializer { *; }
-keepclassmembers class app.sofabird.** { *** Companion; }
-keepclasseswithmembers class app.sofabird.** { kotlinx.serialization.KSerializer serializer(...); }

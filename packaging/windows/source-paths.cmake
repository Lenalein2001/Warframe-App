# Keep diagnostic filenames useful without embedding the build user's profile.
if(MSVC AND CMAKE_CURRENT_SOURCE_DIR STREQUAL CMAKE_SOURCE_DIR)
  set(release_compile "$<AND:$<CONFIG:Release>,$<COMPILE_LANGUAGE:C,CXX>>")
  add_compile_options(
    "$<${release_compile}:/experimental:deterministic>"
    "$<${release_compile}:/pathmap:${COMPANION_SOURCE_ROOT}=.>"
  )
endif()

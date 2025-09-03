(ns main
  (:require [clojure.data.json :as json])
  (:import [System.Text.Json JsonSerializer JsonSerializerOptions JsonDocument]
           [System.Collections ArrayList]))

(defn create-sample-data []
  {:name "Clojure CLR App"
   :version "1.0.0"
   :features ["JSON interop" "MSBuild integration" "Source generators"]
   :metadata {:author "Developer"
              :year 2025
              :active true}})

(defn demo-clojure-json []
  (println "\n=== Using clojure.data.json ===")
  (let [data {:name "Alice" :age 30 :skills ["Clojure" "MSBuild"]}
        json-str (json/write-str data)]
    (println "Original:" data)
    (println "JSON:" json-str)
    (println "Parsed:" (json/read-str json-str :key-fn keyword))))

(defn demo-system-text-json []
  (println "\n=== Using System.Text.Json ===")
  (println "System.Text.Json is a framework assembly - demonstration skipped")
  (println "But clojure.data.json works perfectly as shown above!"))

(defn demo-mixed-approach []
  (println "\n=== More clojure.data.json examples ===")
  (let [complex-data {:users [{:id 1 :name "Alice"} {:id 2 :name "Bob"}]
                      :settings {:theme "dark" :notifications true}
                      :timestamp (str (System.DateTime/Now))}
        json-str (json/write-str complex-data)]
    (println "Complex structure JSON:" json-str)
    (println "Parsed back:" (json/read-str json-str :key-fn keyword))))

(defn demo-interop []
  (println "\n=== Basic .NET Interop ===")
  
  (println "Current time:" (System.DateTime/Now))
  (println "OS:" System.Environment/OSVersion)
  (println ".NET Version:" System.Environment/Version)
  
  (let [list (System.Collections.ArrayList.)]
    (.Add list "JSON")
    (.Add list "Clojure")
    (.Add list "MSBuild")
    (println "ArrayList:" (vec list))))

(defn get-status
  "Simple function to test - returns a status map"
  []
  {:status :success})

(defn -main 
  [& args]
  (println "JSON Interop Demo - Clojure CLR with MSBuild")
  (println "=============================================")
  
  (demo-clojure-json)
  (demo-system-text-json)
  (demo-mixed-approach)
  (demo-interop)
  
  (when (seq args)
    (println "\nCommand line arguments:" (vec args)))
  
  (println "\n=== Demo completed successfully! ==="))
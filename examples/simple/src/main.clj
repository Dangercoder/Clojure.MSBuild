(ns main
  (:require [clojure.data.json :as json])
  (:import [System DateTime]
           [Newtonsoft.Json JsonConvert]))

(defn demonstrate-nuget-integration []
  (println "\n=== Demonstrating NuGet Package Integration ===\n")
  
  ;; Using .NET System libraries
  (println "1. Using .NET DateTime:")
  (let [now (DateTime/Now)]
    (println (str "   Current time: " (.ToString now "yyyy-MM-dd HH:mm:ss"))))
  
  ;; Using Newtonsoft.Json (NuGet package)
  (println "\n2. Using Newtonsoft.Json (NuGet package):")
  (let [data {:message "Hello from Clojure CLR"
              :timestamp (.ToString (DateTime/UtcNow))
              :version 1}
        json-string (JsonConvert/SerializeObject data)]
    (println (str "   Serialized: " json-string))
    (let [deserialized (JsonConvert/DeserializeObject json-string)]
      (println (str "   Deserialized type: " (.GetType deserialized)))))
  
  ;; Using clojure.data.json
  (println "\n3. Using clojure.data.json:")
  (let [clj-data {:greeting "Hello, World!"
                  :numbers [1 2 3 4 5]
                  :nested {:key "value"}}
        json-str (json/write-str clj-data)]
    (println (str "   JSON string: " json-str))
    (println (str "   Parsed back: " (json/read-str json-str)))))

(defn -main [& args]
  (println "Welcome to Clojure CLR with MSBuild Integration!")
  (println (str "Arguments received: " (vec args)))
  (demonstrate-nuget-integration)
  (println "\n=== Example completed successfully! ===")
  (System.Environment/Exit 0))

;; Entry point for REPL testing
(defn run []
  (-main))

;; Run the main function when this file is loaded
(-main)

(comment
  ;; REPL test commands:
  (run)
  (demonstrate-nuget-integration)
  
  ;; Test individual integrations:
  (DateTime/Now)
  (JsonConvert/SerializeObject {:test "data"})
  (json/write-str {:hello "world"})
  )
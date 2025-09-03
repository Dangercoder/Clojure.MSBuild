(ns csharp-interop)

(defn demo-custom-csharp-classes []
  (println "\n=== Using Custom C# Classes from Clojure ===")
  
  ;; Using DataProcessor
  (println "\n1. DataProcessor:")
  (let [processor (ClojureApp.Services.DataProcessor. "MyProcessor")]
    (println "  - Name:" (.Name processor))
    (println "  - Process 'hello':" (.ProcessData processor "hello"))
    (println "  - Process 'world':" (.ProcessData processor "world"))
    (println "  - Count:" (.ProcessedCount processor)))
  
  ;; Using MathUtils static methods
  (println "\n2. MathUtils static methods:")
  (println "  - Add 10 + 20:" (ClojureApp.Services.MathUtils/Add 10 20))
  (println "  - Multiply 5 * 6:" (ClojureApp.Services.MathUtils/Multiply 5 6))
  (println "  - Power 2^10:" (ClojureApp.Services.MathUtils/Power 2.0 10.0))
  (println "  - Format π:" (ClojureApp.Services.MathUtils/FormatNumber 3.14159265 4))
  
  ;; Using Person class
  (println "\n3. Person class:")
  (let [person (ClojureApp.Services.Person. "John" "Doe" 30)]
    (println "  - Full name:" (.GetFullName person))
    (println "  - ToString:" (.ToString person))
    (println "  - Age:" (.Age person))
    ;; Modify age
    (.set_Age person 31)
    (println "  - After birthday:" (.ToString person)))
  
  ;; Using ArrayList (non-generic) which works easily
  (println "\n4. Using .NET collections:")
  (let [numbers (System.Collections.ArrayList.)]
    (doseq [n [5 10 15 20 25]]
      (.Add numbers n))
    (println "  - Numbers:" (vec numbers))
    (println "  - Sum:" (reduce + numbers)))
  
  ;; Combining Clojure and C# seamlessly
  (println "\n5. Seamless integration:")
  (let [people [(ClojureApp.Services.Person. "Alice" "Smith" 25)
                (ClojureApp.Services.Person. "Bob" "Jones" 30)
                (ClojureApp.Services.Person. "Carol" "White" 35)]
        names (map #(.GetFullName %) people)
        ages (map #(.Age %) people)]
    (println "  - Names:" (vec names))
    (println "  - Ages:" (vec ages))
    (println "  - Average age:" (/ (reduce + ages) (count ages)))))